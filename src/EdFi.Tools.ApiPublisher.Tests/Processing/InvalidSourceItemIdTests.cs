// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System;
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
    /// APIPUB-102: A source document with a missing or invalid "id" must produce a controlled,
    /// logged error that is counted in the run's error set, while the remaining documents
    /// continue to be published.
    /// </summary>
    [TestFixture]
    public class InvalidSourceItemIdTests
    {
        private const string ResourcePath = "/ed-fi/stateEducationAgencies";

        // Stands in for nested source data that must never reach the error log by way of the invalid id
        private const string SensitiveNestedValue = "123-45-6789";

        [TestCase("missing")]
        [TestCase("null")]
        [TestCase("object")]
        [TestCase("array")]
        [TestCase("empty")]
        public async Task When_a_source_item_has_an_invalid_id_it_is_reported_as_an_error_and_the_remaining_items_are_still_published(
            string invalidIdVariant)
        {
            // -----------------------------------------------------------------
            //                      Source Requests
            // -----------------------------------------------------------------
            var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

            var pageItems = sourceResourceFaker.Generate(3)
                .Select(resource => JObject.FromObject(resource, JsonSerializer.Create(MockRequests.SerializerSettings)))
                .ToList();

            // Corrupt the "id" of the second item
            var invalidItem = pageItems[1];

            switch (invalidIdVariant)
            {
                case "missing":
                    invalidItem.Remove("id");
                    break;
                case "null":
                    invalidItem["id"] = JValue.CreateNull();
                    break;
                case "object":
                    invalidItem["id"] = new JObject { ["ssn"] = SensitiveNestedValue };
                    break;
                case "array":
                    invalidItem["id"] = new JArray(SensitiveNestedValue);
                    break;
                case "empty":
                    invalidItem["id"] = string.Empty;
                    break;
            }

            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: pageItems.Count)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{ResourcePath}", pageItems);

            // -----------------------------------------------------------------
            //                      Target Requests
            // -----------------------------------------------------------------
            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
            fakeTargetRequestHandler.PostResource($"{EdFiApiConstants.DataManagementApiSegment}{ResourcePath}", HttpStatusCode.OK);

            // -----------------------------------------------------------------
            //                  Source/Target Connection Details
            // -----------------------------------------------------------------
            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(include: new[] { ResourcePath });
            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            // -----------------------------------------------------------------
            //                    Options and Configuration
            // -----------------------------------------------------------------
            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;

            TestHelpers.InitializeLogging();

            var changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

            var publishedErrors = new List<ErrorItemMessage>();
            var errorPublisher = A.Fake<IErrorPublisher>();

            A.CallTo(() => errorPublisher.PublishErrorsAsync(A<ErrorItemMessage[]>.Ignored))
                .Invokes((ErrorItemMessage[] errors) => publishedErrors.AddRange(errors))
                .Returns(Task.CompletedTask);

            var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                sourceApiConnectionDetails,
                fakeSourceRequestHandler,
                targetApiConnectionDetails,
                fakeTargetRequestHandler,
                errorPublisher: errorPublisher);

            using (TestCorrelator.CreateContext())
            {
                // The run must complete without an unhandled exception faulting the pipeline
                await changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None);

                // The two valid items must still be published
                A.CallTo(
                        () => fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{ResourcePath}",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened(2, Times.Exactly);

                // The invalid item must be counted in the run's error set, but its (potentially sensitive)
                // source payload must not be retained -- unlike an ordinary POST failure, there is no target
                // response to correlate it against, so the diagnostic value doesn't justify logging it in full
                var error = publishedErrors.ShouldHaveSingleItem();
                error.Method.ShouldBe(HttpMethod.Post.ToString());
                error.ResourceUrl.ShouldBe(ResourcePath);
                error.Body.ShouldBeNull();

                // The recorded id is a diagnostic only. A non-scalar id (object/array) could carry arbitrary
                // nested source data, so only its token type may be recorded -- never its contents.
                (error.Id ?? string.Empty).ShouldNotContain(SensitiveNestedValue);

                switch (invalidIdVariant)
                {
                    case "object":
                        error.Id.ShouldBe("<invalid id: Object>");
                        break;
                    case "array":
                        error.Id.ShouldBe("<invalid id: Array>");
                        break;
                }

                // A controlled error message must be logged for the operator
                var errorMessages = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Where(logEvent => logEvent.Level == LogEventLevel.Error)
                    .Select(logEvent => logEvent.RenderMessage())
                    .ToList();

                errorMessages.ShouldContain(
                    message => message.Contains(ResourcePath) && message.Contains("'id'"),
                    $"Error log entries: {string.Join(System.Environment.NewLine, errorMessages)}");
            }
        }

        [Test]
        public void When_a_source_deletes_item_has_no_id_the_page_still_yields_every_item_for_processing()
        {
            // The source "id" on a delete item is diagnostic only -- the actual GET-by-key and DELETE
            // operations are driven entirely by "keyValues". A missing/invalid id is not a functional
            // failure, so it must not throw, cancel the page, or drop the sibling items.
            var factory = new DeleteResourceProcessingBlocksFactory(A.Fake<ITargetEdFiApiClientProvider>());

            var message = new StreamResourcePageMessage<GetItemForDeletionMessage>
            {
                ResourceUrl = $"{ResourcePath}{EdFiApiConstants.DeletesPathSuffix}",
                CancellationSource = new CancellationTokenSource(),
            };

            string json = new JArray(
                new JObject
                {
                    ["id"] = "0123456789abcdef0123456789abcdef",
                    ["keyValues"] = new JObject { ["name"] = "ValidItem" },
                },
                new JObject
                {
                    ["keyValues"] = new JObject { ["name"] = "ItemWithoutAnId" },
                }).ToString();

            List<GetItemForDeletionMessage> items = null;

            Should.NotThrow(() => items = factory.CreateProcessDataMessages(message, json).ToList());

            items.Count.ShouldBe(2, "both items -- including the one without an id -- must be yielded");
            items[1].Id.ShouldBeNullOrEmpty();
            items[1].KeyValues.ShouldNotBeNull();
            message.CancellationSource.IsCancellationRequested.ShouldBeFalse();
        }

        [Test]
        public async Task When_a_source_item_has_an_invalid_id_the_run_reports_failure_via_the_published_error_count()
        {
            // APIPUB-120: the terminal summary and exit code are driven entirely by
            // IErrorPublisher.GetPublishedErrorCount() (see ChangeProcessor.EnsureProcessingWasSuccessful).
            // A fake that always returns 0 (as used elsewhere in this fixture) cannot prove that path is
            // reached -- this fake actually tracks what PublishErrorsAsync received.
            var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

            var pageItems = sourceResourceFaker.Generate(1)
                .Select(resource => JObject.FromObject(resource, JsonSerializer.Create(MockRequests.SerializerSettings)))
                .ToList();

            pageItems[0].Remove("id");

            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: pageItems.Count)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{ResourcePath}", pageItems);

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(include: new[] { ResourcePath });
            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;

            TestHelpers.InitializeLogging();

            var changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

            long publishedErrorCount = 0;
            var errorPublisher = A.Fake<IErrorPublisher>();

            A.CallTo(() => errorPublisher.PublishErrorsAsync(A<ErrorItemMessage[]>.Ignored))
                .Invokes((ErrorItemMessage[] messages) => Interlocked.Add(ref publishedErrorCount, messages.Length))
                .Returns(Task.CompletedTask);

            A.CallTo(() => errorPublisher.GetPublishedErrorCount())
                .ReturnsLazily(() => Interlocked.Read(ref publishedErrorCount));

            var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                sourceApiConnectionDetails,
                fakeSourceRequestHandler,
                targetApiConnectionDetails,
                fakeTargetRequestHandler,
                errorPublisher: errorPublisher);

            // The run must fail (which the CLI's generic catch turns into a non-zero exit code)
            var caught = await Should.ThrowAsync<Exception>(
                () => changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None));

            caught.Message.ShouldContain("did not complete successfully");
            publishedErrorCount.ShouldBe(1);
        }

        [Test]
        public async Task When_an_authorization_retry_is_deferred_the_id_survives_the_release_of_Item()
        {
            // Reproduces the actual race (not just the symptom): a Forbidden POST with a configured retry
            // delegate re-queues the SAME PostItemMessage object, and the original invocation's finally
            // block then releases Item on that same object. The id must already be stamped by then so
            // that a later re-entry for this object (or a failure report about it) can identify it.
            TestHelpers.InitializeLogging();

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
            fakeTargetRequestHandler.PostResource($"{EdFiApiConstants.DataManagementApiSegment}{ResourcePath}", HttpStatusCode.Forbidden);

            EdFiApiClient TargetApiClientFactory() =>
                new EdFiApiClient(
                    "TestTarget",
                    TestHelpers.GetTargetApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeTargetRequestHandler));

            var factory = new PostResourceProcessingBlocksFactory(
                A.Fake<INodeJSService>(),
                new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory)),
                TestHelpers.GetSourceApiConnectionDetails(),
                A.Fake<ISourceCapabilities>(),
                A.Fake<ISourceResourceItemProvider>());

            var (ingestionBlock, outputBlock) = factory.CreateProcessingBlocks(
                new CreateBlocksRequest(
                    TestHelpers.GetOptions(),
                    TestHelpers.Configuration.GetAuthorizationFailureHandling(),
                    new BufferBlock<ErrorItemMessage>(),
                    javaScriptModuleFactory: null));

            PostItemMessage retriedMessage = null;

            var message = new PostItemMessage
            {
                ResourceUrl = ResourcePath,
                Item = new JObject { ["id"] = "0123456789abcdef0123456789abcdef" },
                PostAuthorizationFailureRetry = msg => retriedMessage = (PostItemMessage)msg,
            };

            ingestionBlock.Post(message);
            ingestionBlock.Complete();

            while (await outputBlock.OutputAvailableAsync())
            {
                await outputBlock.ReceiveAsync();
            }

            await outputBlock.Completion;

            retriedMessage.ShouldBeSameAs(message, "the retry delegate re-queues the SAME message object");
            message.Item.ShouldBeNull("the finally block releases Item after the retry delegate has been invoked");
            message.Id.ShouldBe(
                "0123456789abcdef0123456789abcdef",
                "the id must survive so a subsequent retry invocation can identify the item");
        }

        [Test]
        public async Task When_a_post_item_no_longer_has_its_data_it_is_reported_as_an_error_instead_of_faulting_the_block()
        {
            // A deferred authorization-failure retry re-queues the same PostItemMessage object into the
            // "#Retry" processing block, but the original handler's finally block has already released its
            // Item reference -- so the retry block receives a message whose Item is null (APIPUB-102's
            // production stack trace). The block must report a controlled error instead of faulting.
            TestHelpers.InitializeLogging();

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            EdFiApiClient TargetApiClientFactory() =>
                new EdFiApiClient(
                    "TestTarget",
                    TestHelpers.GetTargetApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeTargetRequestHandler));

            var factory = new PostResourceProcessingBlocksFactory(
                A.Fake<INodeJSService>(),
                new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory)),
                TestHelpers.GetSourceApiConnectionDetails(),
                A.Fake<ISourceCapabilities>(),
                A.Fake<ISourceResourceItemProvider>());

            var (ingestionBlock, outputBlock) = factory.CreateProcessingBlocks(
                new CreateBlocksRequest(
                    TestHelpers.GetOptions(),
                    TestHelpers.Configuration.GetAuthorizationFailureHandling(),
                    new BufferBlock<ErrorItemMessage>(),
                    javaScriptModuleFactory: null));

            using (TestCorrelator.CreateContext())
            {
                // The id is stashed on the message when it was first created from the source page, so it
                // survives even after Item is released -- giving the operator a document to investigate.
                ingestionBlock.Post(
                    new PostItemMessage { ResourceUrl = "/ed-fi/students", Item = null, Id = "0123456789abcdef0123456789abcdef" });
                ingestionBlock.Complete();

                var errors = new List<ErrorItemMessage>();

                while (await outputBlock.OutputAvailableAsync())
                {
                    errors.Add(await outputBlock.ReceiveAsync());
                }

                // The block must complete without faulting (a fault silently drops all remaining queued items)
                await outputBlock.Completion;

                var error = errors.ShouldHaveSingleItem();
                error.Method.ShouldBe(HttpMethod.Post.ToString());
                error.ResourceUrl.ShouldBe("/ed-fi/students");
                error.Id.ShouldBe("0123456789abcdef0123456789abcdef");

                var errorMessages = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Where(logEvent => logEvent.Level == LogEventLevel.Error)
                    .Select(logEvent => logEvent.RenderMessage())
                    .ToList();

                errorMessages.ShouldContain(
                    message => message.Contains("/ed-fi/students") && message.Contains("0123456789abcdef0123456789abcdef"),
                    $"Error log entries: {string.Join(System.Environment.NewLine, errorMessages)}");
            }
        }

        [Test]
        public void When_a_source_keyChanges_item_has_no_id_the_page_still_yields_every_item_for_processing()
        {
            // The source "id" on a key-change item is diagnostic only -- the target item to update is found
            // via "oldKeyValues" and identified by its own returned id. A missing/invalid source id is not a
            // functional failure, so it must not throw, cancel the page, or drop the sibling items.
            var factory = new ChangeResourceKeyProcessingBlocksFactory(A.Fake<ITargetEdFiApiClientProvider>());

            var message = new StreamResourcePageMessage<GetItemForKeyChangeMessage>
            {
                ResourceUrl = $"{ResourcePath}{EdFiApiConstants.KeyChangesPathSuffix}",
                CancellationSource = new CancellationTokenSource(),
            };

            string json = new JArray(
                new JObject
                {
                    ["id"] = "0123456789abcdef0123456789abcdef",
                    ["oldKeyValues"] = new JObject { ["name"] = "OldName" },
                    ["newKeyValues"] = new JObject { ["name"] = "NewName" },
                },
                new JObject
                {
                    ["oldKeyValues"] = new JObject { ["name"] = "OldNameOfItemWithoutAnId" },
                    ["newKeyValues"] = new JObject { ["name"] = "NewNameOfItemWithoutAnId" },
                }).ToString();

            List<GetItemForKeyChangeMessage> items = null;

            Should.NotThrow(() => items = factory.CreateProcessDataMessages(message, json).ToList());

            items.Count.ShouldBe(2, "both items -- including the one without an id -- must be yielded");
            items[1].SourceId.ShouldBeNullOrEmpty();
            items[1].ExistingKeyValues.ShouldNotBeNull();
            message.CancellationSource.IsCancellationRequested.ShouldBeFalse();
        }
    }
}
