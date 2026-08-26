// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Covers the missing-dependency resolution path of the POST processing block: when a "primary
    /// relationship" resource fails with an unresolved reference, the referenced item is fetched from the
    /// source and posted to the target before the original item is retried. Verifies that the source lookup
    /// honors the item's cancellation token and that a Forbidden response on the dependency post is deferred
    /// (or not) based on the DEPENDENCY resource's own authorization-retry pipeline rather than the current
    /// item's (see PR #152 review).
    /// </summary>
    [TestFixture]
    public class MissingDependencyResolutionTests
    {
        private const string StudentSchoolAssociations = "/ed-fi/studentSchoolAssociations";
        private const string Students = "/ed-fi/students";
        private const string MissingStudentHref = "/ed-fi/students/abc123";

        [Test]
        public async Task Source_lookup_for_the_missing_dependency_should_observe_the_items_cancellation_token()
        {
            TestHelpers.InitializeLogging();

            var fakeTargetRequestHandler = CreateTargetHandlerWithUnresolvedStudentReference(studentPostStatus: HttpStatusCode.OK);

            var sourceResourceItemProvider = CreateProviderReturningMissingStudent();

            using var cancellationSource = new CancellationTokenSource();

            var message = CreateStudentSchoolAssociationMessage(cancellationSource.Token, hasAuthorizationRetryPipeline: false);

            var errors = await ProcessAsync(
                message,
                fakeTargetRequestHandler,
                sourceResourceItemProvider,
                authorizationRetryPipelineResourcePaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            errors.ShouldBeEmpty();

            // The lookup must receive the item's own token (not CancellationToken.None) so cancelling the run
            // also releases a handler blocked on the source GET or its retry delays
            A.CallTo(() => sourceResourceItemProvider.TryGetResourceItemAsync(MissingStudentHref, cancellationSource.Token))
                .MustHaveHappenedOnceExactly();
        }

        // The current item's marker is deliberately the OPPOSITE of the dependency's in both cases: copying it
        // to the dependency post (the previous behavior) is wrong in either direction
        [TestCase(true, false)]  // dependency has its own retry pass -> Forbidden deferred, no error published
        [TestCase(false, true)]  // dependency has no retry pass      -> Forbidden must surface as an error
        public async Task Forbidden_dependency_post_should_be_deferred_only_when_the_dependency_resource_has_a_retry_pipeline(
            bool dependencyHasRetryPipeline,
            bool currentItemHasRetryPipeline)
        {
            TestHelpers.InitializeLogging();

            var fakeTargetRequestHandler = CreateTargetHandlerWithUnresolvedStudentReference(studentPostStatus: HttpStatusCode.Forbidden);

            var sourceResourceItemProvider = CreateProviderReturningMissingStudent();

            var message = CreateStudentSchoolAssociationMessage(CancellationToken.None, currentItemHasRetryPipeline);

            var retryPipelineResourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                dependencyHasRetryPipeline ? Students : StudentSchoolAssociations
            };

            // The dependency post's outcome is only observable through the log: its handler runs nested inside
            // the current item's retry policy and its error messages are not propagated to the error stream
            IReadOnlyList<LogEvent> logEvents;

            using (TestCorrelator.CreateContext())
            {
                await ProcessAsync(message, fakeTargetRequestHandler, sourceResourceItemProvider, retryPipelineResourcePaths);

                logEvents = TestCorrelator.GetLogEventsFromCurrentContext().ToList();
            }

            // The dependency was fetched and posted exactly once, and the original item was retried afterwards
            A.CallTo(() => fakeTargetRequestHandler.Post(A<string>.That.EndsWith(Students), A<HttpRequestMessage>.Ignored))
                .MustHaveHappenedOnceExactly();

            A.CallTo(() => fakeTargetRequestHandler.Post(A<string>.That.EndsWith(StudentSchoolAssociations), A<HttpRequestMessage>.Ignored))
                .MustHaveHappenedTwiceExactly();

            var studentForbiddenErrors = logEvents
                .Where(e => e.Level == LogEventLevel.Error)
                .Select(e => e.RenderMessage())
                .Where(m => m.Contains(Students) && m.Contains(nameof(HttpStatusCode.Forbidden)))
                .ToList();

            var studentDeferrals = logEvents
                .Select(e => e.RenderMessage())
                .Where(m => m.Contains(Students) && m.Contains("re-published by the authorization retry pass"))
                .ToList();

            if (dependencyHasRetryPipeline)
            {
                // Deferred to the students "#Retry" pass -- nothing to report as an error
                studentForbiddenErrors.ShouldBeEmpty();
                studentDeferrals.ShouldHaveSingleItem();
            }
            else
            {
                // No retry pass will ever re-publish the student, so the failure must not be silently discarded
                studentForbiddenErrors.ShouldHaveSingleItem();
                studentDeferrals.ShouldBeEmpty();
            }
        }

        private static IFakeHttpRequestHandler CreateTargetHandlerWithUnresolvedStudentReference(HttpStatusCode studentPostStatus)
        {
            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            // First POST of the association reports the unresolved student reference; the retry (after the
            // dependency has been posted) succeeds
            fakeTargetRequestHandler.PostResource(
                $"{EdFiApiConstants.DataManagementApiSegment}{StudentSchoolAssociations}",
                (HttpStatusCode.BadRequest, JObject.Parse("{ \"message\": \"Validation of 'StudentSchoolAssociation' failed.\\r\\n\\tStudent reference could not be resolved.\\n\" }")),
                (HttpStatusCode.OK, null));

            fakeTargetRequestHandler.PostResource($"{EdFiApiConstants.DataManagementApiSegment}{Students}", studentPostStatus);

            return fakeTargetRequestHandler;
        }

        private static ISourceResourceItemProvider CreateProviderReturningMissingStudent()
        {
            var sourceResourceItemProvider = A.Fake<ISourceResourceItemProvider>();

            A.CallTo(() => sourceResourceItemProvider.TryGetResourceItemAsync(A<string>.Ignored, A<CancellationToken>.Ignored))
                .Returns((true, "{ \"id\": \"abc123\", \"studentUniqueId\": \"S-1\", \"firstName\": \"Bob\", \"lastSurname\": \"Jones\", \"_etag\": \"etag\" }"));

            return sourceResourceItemProvider;
        }

        private static PostItemMessage CreateStudentSchoolAssociationMessage(CancellationToken cancellationToken, bool hasAuthorizationRetryPipeline)
        {
            return new PostItemMessage
            {
                ResourceUrl = StudentSchoolAssociations,
                Item = new JObject
                {
                    ["id"] = Guid.NewGuid().ToString("n"),
                    ["studentReference"] = new JObject
                    {
                        ["studentUniqueId"] = "S-1",
                        ["link"] = new JObject
                        {
                            ["rel"] = "Student",
                            ["href"] = MissingStudentHref,
                        },
                    },
                    ["entryDate"] = "2024-08-01",
                },
                HasAuthorizationRetryPipeline = hasAuthorizationRetryPipeline,
                CancellationToken = cancellationToken,
            };
        }

        private static async Task<IReadOnlyCollection<ErrorItemMessage>> ProcessAsync(
            PostItemMessage message,
            IFakeHttpRequestHandler fakeTargetRequestHandler,
            ISourceResourceItemProvider sourceResourceItemProvider,
            IReadOnlySet<string> authorizationRetryPipelineResourcePaths)
        {
            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            // A Forbidden response that is NOT deferred must surface as an error rather than a warning
            targetApiConnectionDetails.TreatForbiddenPostAsWarning = false;

            EdFiApiClient TargetApiClientFactory() =>
                new EdFiApiClient(
                    "TestTarget",
                    targetApiConnectionDetails,
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeTargetRequestHandler));

            var sourceCapabilities = A.Fake<ISourceCapabilities>();
            A.CallTo(() => sourceCapabilities.SupportsGetItemById).Returns(true);

            var factory = new PostResourceProcessingBlocksFactory(
                A.Fake<INodeJSService>(),
                new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory)),
                TestHelpers.GetSourceApiConnectionDetails(),
                sourceCapabilities,
                sourceResourceItemProvider);

            var authorizationFailureHandling = new[]
            {
                new AuthorizationFailureHandling
                {
                    Path = Students,
                    UpdatePrerequisitePaths = new[] { StudentSchoolAssociations }
                }
            };

            var createBlocksRequest = new CreateBlocksRequest(
                TestHelpers.GetOptions(),
                authorizationFailureHandling,
                new BufferBlock<ErrorItemMessage>(),
                javaScriptModuleFactory: null,
                authorizationRetryPipelineResourcePaths);

            var (inputBlock, outputBlock) = factory.CreateProcessingBlocks(createBlocksRequest);

            var errors = new ConcurrentQueue<ErrorItemMessage>();
            var errorSink = new ActionBlock<ErrorItemMessage>(errors.Enqueue);
            outputBlock.LinkTo(errorSink, new DataflowLinkOptions { PropagateCompletion = true });

            (await inputBlock.SendAsync(message)).ShouldBeTrue();
            inputBlock.Complete();

            await errorSink.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            return errors;
        }
    }
}
