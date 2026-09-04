// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// End-to-end coverage of the authorization-retry ("#Retry") pipeline (see APIPUB-133): items that are
    /// initially Forbidden must be re-published successfully by the retry pass -- which re-streams the entire
    /// resource once its update prerequisites complete -- without publishing errors, without losing items, and
    /// without deadlocking now that retry pipelines are bounded like every other pipeline.
    /// </summary>
    [TestFixture]
    public class AuthorizationRetryTests
    {
        private const string Students = "/ed-fi/students";
        private const string StudentSchoolAssociations = "/ed-fi/studentSchoolAssociations";

        [TestCase(0)] // automatic capacity
        [TestCase(1)] // explicit floor capacity -- saturates the bounded retry pipeline (no deadlock, no loss)
        public async Task Forbidden_items_should_be_republished_by_the_bounded_retry_pass_after_prerequisites_complete(
            int configuredCapacity)
        {
            TestHelpers.InitializeLogging();

            // Deliberately fewer than the configured StreamingPageSize (50): the single-page fake below returns
            // the same list for ANY offset, so a full page (count == limit) would trip the final-page
            // continuation check into fetching further pages forever
            const int TotalItems = 25;

            var suppliedPageOfResources = TestHelpers.GetGenericResourceFaker().Generate(TotalItems);

            // Full-catalog publish: the inclusion filters strip retry entries for non-included paths
            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: TotalItems)
                .GetResourceData($@"/data/v3{TestHelpers.AnyResourcePattern}", suppliedPageOfResources);

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            // Catch-all first; the students-specific ordered registration below takes precedence for its URL
            fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

            // Every student POST of the main pass is Forbidden; every subsequent POST (the retry pass,
            // which re-streams the entire resource after studentSchoolAssociations completes) succeeds
            fakeTargetRequestHandler.PostResource(
                $"{EdFiApiConstants.DataManagementApiSegment}{Students}$",
                Enumerable.Repeat(HttpStatusCode.Forbidden, TotalItems).Append(HttpStatusCode.OK).ToArray());

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();

            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            // Ensure a Forbidden response that is NOT deferred to the retry pass would surface as a published
            // error rather than being masked as a warning
            targetApiConnectionDetails.TreatForbiddenPostAsWarning = false;

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;
            options.ProcessingBlockBoundedCapacity = configuredCapacity;

            var authorizationFailureHandling = new[]
            {
                new AuthorizationFailureHandling
                {
                    Path = Students,
                    UpdatePrerequisitePaths = new[] { StudentSchoolAssociations }
                }
            };

            var changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(
                options,
                authorizationFailureHandling: authorizationFailureHandling);

            var errorPublisher = A.Fake<IErrorPublisher>();

            // Capture every published error so a failure names the offending resource instead of just "found it once"
            var publishedErrors = new ConcurrentQueue<ErrorItemMessage>();

            A.CallTo(() => errorPublisher.PublishErrorsAsync(A<ErrorItemMessage[]>.Ignored))
                .Invokes((ErrorItemMessage[] messages) =>
                {
                    foreach (var errorItemMessage in messages)
                    {
                        publishedErrors.Enqueue(errorItemMessage);
                    }
                });

            var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                sourceApiConnectionDetails,
                fakeSourceRequestHandler,
                targetApiConnectionDetails,
                fakeTargetRequestHandler,
                errorPublisher: errorPublisher);

            // Task.Run: the fake HTTP handlers complete synchronously, so the blocking publish loop would
            // otherwise run entirely on this thread. The timeout also guards the "no deadlock under
            // saturated retry" criterion for the bounded test case.
            await Task.Run(
                    () => changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(120));

            // Every student was posted twice: once by the main pass (Forbidden, deferred without an error)
            // and once by the retry pass (succeeding) -- no item was lost
            A.CallTo(
                    () => fakeTargetRequestHandler.Post(
                        A<string>.That.Matches(url => url.EndsWith(Students)),
                        A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(2 * TotalItems, Times.Exactly);

            // The prerequisite resource published normally, exactly once
            A.CallTo(
                    () => fakeTargetRequestHandler.Post(
                        A<string>.That.Matches(url => url.EndsWith(StudentSchoolAssociations)),
                        A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(TotalItems, Times.Exactly);

            // The deferred Forbidden responses must not have produced any published errors for the resources under
            // test (the retry semantics being proven)...
            publishedErrors
                .Where(e => e.ResourceUrl.EndsWith(Students) || e.ResourceUrl.EndsWith(StudentSchoolAssociations))
                .Select(DescribeError)
                .ShouldBeEmpty();

            // ...and the full-catalog run completed with no errors at all (self-describing, so an unrelated failure
            // elsewhere in the dependency graph is reported by resource rather than mistaken for a retry defect)
            publishedErrors.Select(DescribeError).ShouldBeEmpty();
        }

        private static string DescribeError(ErrorItemMessage error)
            => $"{error.Method} {error.ResourceUrl} -> {error.ResponseStatus?.ToString() ?? error.Exception?.GetType().Name ?? "(no status)"}";
    }
}
