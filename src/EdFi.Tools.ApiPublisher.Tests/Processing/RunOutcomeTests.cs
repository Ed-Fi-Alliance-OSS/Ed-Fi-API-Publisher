// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Metadata;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Polly;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Covers what a run reports when it finishes with documents that were not published: the outcome the
    /// exit code is derived from, the counts reported per resource, and what the configured tolerance does
    /// and does not change (see APIPUB-120).
    /// </summary>
    [TestFixture]
    public class RunOutcomeTests
    {
        private const string StateEducationAgencies = "/ed-fi/stateEducationAgencies";

        [Test]
        public async Task When_documents_are_rejected_the_run_fails_and_names_the_item_error_outcome()
        {
            var run = await RunWithPostResponsesAsync(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.OK);

            var caught = run.Caught.ShouldBeOfType<PublishingFailedException>();

            caught.Reason.ShouldBe(PublishingFailureReason.ItemErrors);
            caught.ItemErrorCount.ShouldBe(2);
            caught.IncompleteResourceCount.ShouldBe(0);

            PublisherExitCode.ForFailure(caught).ShouldBe(PublisherExitCode.CompletedWithItemErrors);
        }

        [Test]
        public async Task When_documents_are_rejected_the_run_summary_accounts_for_them_by_resource()
        {
            var run = await RunWithPostResponsesAsync(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.OK);

            var summary = run.RunSummaryCollector.GetSummary();

            var upserts = summary.Resources
                .Where(resource => resource.Stage == PublishingStage.Upserts && resource.AttemptedItemCount > 0)
                .ShouldHaveSingleItem();

            upserts.ResourcePath.ShouldBe(StateEducationAgencies);
            upserts.ExpectedItemCount.ShouldBe(3);
            upserts.AttemptedItemCount.ShouldBe(3);
            upserts.FailedItemCount.ShouldBe(2);
            upserts.SkippedItemCount.ShouldBe(0);
            upserts.PublishedItemCount.ShouldBe(1);
            summary.SourceReadErrorCount.ShouldBe(0);

            // The rendered table is what an operator actually reads, so the counts have to survive
            // formatting in the right order: the columns are positional, and transposing two of them would
            // otherwise pass every assertion above
            string report = RunSummaryFormatter.Format(summary);

            string upsertsLine = report
                .Split(Environment.NewLine)
                .Single(line => line.TrimStart().StartsWith("upserts"));

            upsertsLine.ShouldMatch(@"upserts\s+" + Regex.Escape(StateEducationAgencies) + @"\s+3\s+3\s+2\s+0\s+1\s*$");
            report.ShouldContain("Publishing run summary");
        }

        [Test]
        public async Task The_run_reports_its_summary_through_the_log()
        {
            // The summary is only useful if the run itself emits it: the tests above assemble it from the
            // collector, which would still pass if the reporting call were removed from the processor
            using (TestCorrelator.CreateContext())
            {
                await RunWithPostResponsesAsync(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.OK);

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .Select(logEvent => logEvent.RenderMessage())
                    .ShouldContain(message => message.Contains("Publishing run summary") && message.Contains(StateEducationAgencies));
            }
        }

        [Test]
        public void A_failure_that_is_not_about_documents_reports_its_own_exit_code()
        {
            PublisherExitCode.ForFailure(
                    new PublishingFailedException("broken", PublishingFailureReason.IncompleteProcessing))
                .ShouldBe(PublisherExitCode.ProcessingIncomplete);

            PublisherExitCode.ForFailure(new InvalidConfigurationException("bad options"))
                .ShouldBe(PublisherExitCode.InvalidConfiguration);

            // Anything unrecognized is a run that did not complete, never a success
            PublisherExitCode.ForFailure(new InvalidOperationException("unexpected"))
                .ShouldBe(PublisherExitCode.ProcessingIncomplete);
        }

        [Test]
        public async Task When_the_failed_document_count_is_within_the_tolerance_the_run_reports_success()
        {
            var run = await RunWithPostResponsesAsync(
                new[] { HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.OK },
                toleratedItemErrorCount: 2);

            run.Caught.ShouldBeNull();

            // Tolerated, not hidden: the documents are still reported as failures
            run.RunSummaryCollector.GetSummary().Resources
                .Single(resource => resource.Stage == PublishingStage.Upserts && resource.AttemptedItemCount > 0)
                .FailedItemCount.ShouldBe(2);
        }

        [Test]
        public async Task When_the_failed_document_count_exceeds_the_tolerance_the_run_fails()
        {
            var run = await RunWithPostResponsesAsync(
                new[] { HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.OK },
                toleratedItemErrorCount: 1);

            run.Caught.ShouldBeOfType<PublishingFailedException>().Reason.ShouldBe(PublishingFailureReason.ItemErrors);
        }

        [Test]
        public async Task When_documents_are_lost_the_last_change_version_processed_is_not_updated_even_when_tolerated()
        {
            var run = await RunWithPostResponsesAsync(
                new[] { HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.OK },
                toleratedItemErrorCount: -1);

            run.Caught.ShouldBeNull();

            // The failed documents are inside the change window, so the window has to be re-published for
            // them to get another chance. Advancing the change version would make them unrecoverable.
            A.CallTo(() => run.ChangeVersionProcessedWriter.SetProcessedChangeVersionAsync(
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<long>.Ignored,
                    A<IConfigurationSection>.Ignored))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task When_every_document_is_published_the_last_change_version_processed_is_updated()
        {
            // The positive control for the assertion above: the same harness has to be able to record a
            // change version, or the test above would pass for the wrong reason
            var run = await RunWithPostResponsesAsync(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK);

            run.Caught.ShouldBeNull();

            A.CallTo(() => run.ChangeVersionProcessedWriter.SetProcessedChangeVersionAsync(
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<long>.Ignored,
                    A<IConfigurationSection>.Ignored))
                .MustHaveHappened();
        }

        [Test]
        public async Task A_document_dropped_by_the_rate_limiter_is_reported_as_an_error()
        {
            // The rate limiter has already exhausted its own retries by the time a rejection reaches the
            // handler, so the document was not published. It used to be dropped with only a log entry, which
            // let a run lose documents and still report success (APIPUB-120).
            var rateLimiter = A.Fake<IRateLimiting<HttpResponseMessage>>();

            A.CallTo(() => rateLimiter.GetRateLimitingPolicy())
                .Returns(Policy.RateLimitAsync<HttpResponseMessage>(1, TimeSpan.FromMinutes(10)));

            var run = await RunWithPostResponsesAsync(
                new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK },
                toleratedItemErrorCount: 0,
                enableRateLimit: true,
                rateLimiter: rateLimiter);

            var caught = run.Caught.ShouldBeOfType<PublishingFailedException>();
            caught.Reason.ShouldBe(PublishingFailureReason.ItemErrors);

            // The limiter admits one document; the rest are rejected and must be accounted for as failures
            var upserts = run.RunSummaryCollector.GetSummary().Resources
                .Single(resource => resource.Stage == PublishingStage.Upserts && resource.AttemptedItemCount > 0);

            upserts.AttemptedItemCount.ShouldBe(3);
            upserts.FailedItemCount.ShouldBe(2);
            upserts.PublishedItemCount.ShouldBe(1);
        }

        [Test]
        public async Task Documents_abandoned_after_an_authorization_failure_are_skipped_not_published()
        {
            // The target connection is configured with treatForbiddenPostAsWarning, so a 403 abandons the
            // rest of the resource by the operator's own choice. Those documents are neither published nor
            // rejected, and counting them as published is exactly the silent loss this work is about.
            var run = await RunWithPostResponsesAsync(HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.OK);

            var upserts = run.RunSummaryCollector.GetSummary().Resources
                .Single(resource => resource.Stage == PublishingStage.Upserts && resource.AttemptedItemCount > 0);

            upserts.AttemptedItemCount.ShouldBe(3);
            upserts.SkippedItemCount.ShouldBe(3);
            upserts.FailedItemCount.ShouldBe(0);
            upserts.PublishedItemCount.ShouldBe(0);

            run.RunSummaryCollector.GetSummary().SkipReasons
                .ShouldContain(SkipReasons.ResourceIgnoredAfterAuthorizationFailure);

            // Abandoning by configuration is not an error, so the run itself still succeeds
            run.Caught.ShouldBeNull();
        }

        [Test]
        public async Task A_source_that_cannot_be_read_fails_the_run_whatever_the_tolerance_allows()
        {
            // The documents behind an unread page were never attempted and their number is not known, so no
            // threshold can honestly cover them
            var run = await RunWithPostResponsesAsync(
                new[] { HttpStatusCode.OK },
                toleratedItemErrorCount: -1,
                sourceReadFails: true);

            var caught = run.Caught.ShouldBeOfType<PublishingFailedException>();

            caught.Reason.ShouldBe(PublishingFailureReason.IncompleteProcessing);
            PublisherExitCode.ForFailure(caught).ShouldBe(PublisherExitCode.ProcessingIncomplete);
            run.RunSummaryCollector.GetSummary().SourceReadErrorCount.ShouldBeGreaterThan(0);
        }

        [Test]
        public async Task The_authorization_retry_pass_does_not_count_its_documents_twice()
        {
            // A resource configured for authorization retry is streamed a second time under the same resource
            // URL once its prerequisites complete, which used to double every count in its row. The shipped
            // settings configure this for students, staffs and contacts, so it is the default reading of the
            // three highest-volume resources of an ordinary publish.
            const string StudentsResource = "/ed-fi/students";

            var suppliedSourceResources = TestHelpers.GetGenericResourceFaker().Generate(2);

            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 2)
                .GetResourceData(
                    $"{EdFiApiConstants.DataManagementApiSegment}{StudentsResource}",
                    suppliedSourceResources.ToArray());

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
            fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

            var run = await RunStudentsWithRetryPassAsync(fakeSourceRequestHandler, fakeTargetRequestHandler);

            // The positive control: the pipeline really did publish each document twice, once per pass
            A.CallTo(
                    () => fakeTargetRequestHandler.Post(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{StudentsResource}",
                        A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(4, Times.Exactly);

            // ... and the summary still reports the two documents the resource actually has
            var resource = run.RunSummaryCollector.GetSummary().Resources
                .Single(r => r.Stage == PublishingStage.Upserts && r.ResourcePath == StudentsResource);

            resource.AttemptedItemCount.ShouldBe(2);
            resource.PublishedItemCount.ShouldBe(2);
            resource.FailedItemCount.ShouldBe(0);
        }

        [Test]
        public async Task A_document_rejected_on_both_passes_is_reported_once_and_the_retry_pass_is_named()
        {
            // Two documents rejected by the target on the first pass and again by the authorization retry
            // pass produce four error records. Counting records rather than documents reported four failures
            // against two attempted documents, and consumed the operator's tolerance twice as fast.
            const string StudentsResource = "/ed-fi/students";

            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 2)
                .GetResourceData(
                    $"{EdFiApiConstants.DataManagementApiSegment}{StudentsResource}",
                    TestHelpers.GetGenericResourceFaker().Generate(2).ToArray());

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler()
                .PostResource(
                    $"{EdFiApiConstants.DataManagementApiSegment}{StudentsResource}",
                    HttpStatusCode.BadRequest,
                    HttpStatusCode.BadRequest,
                    HttpStatusCode.BadRequest,
                    HttpStatusCode.BadRequest);

            var run = await RunStudentsWithRetryPassAsync(fakeSourceRequestHandler, fakeTargetRequestHandler);

            var resource = run.RunSummaryCollector.GetSummary().Resources
                .Single(r => r.Stage == PublishingStage.Upserts && r.ResourcePath == StudentsResource);

            resource.AttemptedItemCount.ShouldBe(2);
            resource.FailedItemCount.ShouldBe(2);
            resource.PublishedItemCount.ShouldBe(0);

            // The pass that decided the outcome is named, with what the first one had reported
            resource.AuthorizationRetryPass.ShouldNotBeNull();
            resource.AuthorizationRetryPass.FirstPassFailedItemCount.ShouldBe(2);
            resource.AuthorizationRetryPass.ReattemptedItemCount.ShouldBe(2);
            resource.AuthorizationRetryPass.FailedItemCount.ShouldBe(2);

            RunSummaryFormatter.Format(run.RunSummaryCollector.GetSummary())
                .ShouldContain("the authorization retry pass republished");
        }

        private static async Task<RunResult> RunStudentsWithRetryPassAsync(
            IFakeHttpRequestHandler fakeSourceRequestHandler,
            IFakeHttpRequestHandler fakeTargetRequestHandler)
        {
            // "/ed-fi/students" is configured for authorization retry by the shipped settings, so the resource
            // is streamed a second time once "/ed-fi/studentSchoolAssociations" completes
            const string StudentsResource = "/ed-fi/students";

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;

            TestHelpers.InitializeLogging();

            var errorPublisher = new SerilogErrorPublisher();
            var metadataCollector = new PublishingOperationMetadataCollector();
            var runSummaryCollector = new RunSummaryCollector(metadataCollector);
            var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();

            var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                TestHelpers.GetSourceApiConnectionDetails(include: new[] { StudentsResource }),
                fakeSourceRequestHandler,
                TestHelpers.GetTargetApiConnectionDetails(),
                fakeTargetRequestHandler,
                errorPublisher: errorPublisher,
                runSummaryCollector: runSummaryCollector,
                metadataCollector: metadataCollector,
                changeVersionProcessedWriter: changeVersionProcessedWriter);

            Exception caught = null;

            try
            {
                await changeProcessor.ProcessChangesAsync(
                    TestHelpers.CreateChangeProcessorConfiguration(options),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            return new RunResult(caught, errorPublisher, runSummaryCollector, changeVersionProcessedWriter);
        }

        private static Task<RunResult> RunWithPostResponsesAsync(params HttpStatusCode[] postResponseCodes)
        {
            return RunWithPostResponsesAsync(postResponseCodes, toleratedItemErrorCount: 0);
        }

        private static async Task<RunResult> RunWithPostResponsesAsync(
            HttpStatusCode[] postResponseCodes,
            int toleratedItemErrorCount,
            bool enableRateLimit = false,
            IRateLimiting<HttpResponseMessage> rateLimiter = null,
            bool sourceReadFails = false)
        {
            var suppliedSourceResources = TestHelpers.GetGenericResourceFaker().Generate(3);

            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 3)
                .GetResourceData(
                    $"{EdFiApiConstants.DataManagementApiSegment}{StateEducationAgencies}",
                    suppliedSourceResources.ToArray());

            if (sourceReadFails)
            {
                // Every read of the resource fails permanently, so the source-side handlers publish their own
                // error rather than any document being rejected by the target
                A.CallTo(
                        () => fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{StateEducationAgencies}",
                            A<HttpRequestMessage>.Ignored))
                    .ReturnsLazily(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                    });
            }

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler()
                .PostResource($"{EdFiApiConstants.DataManagementApiSegment}{StateEducationAgencies}", postResponseCodes);

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(include: new[] { StateEducationAgencies });
            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;
            options.ToleratedItemErrorCount = toleratedItemErrorCount;
            options.EnableRateLimit = enableRateLimit;

            TestHelpers.InitializeLogging();

            // The real error publisher and collectors: the default fakes report no published errors and an
            // empty summary, which would hide the very signals under test
            var errorPublisher = new SerilogErrorPublisher();
            var metadataCollector = new PublishingOperationMetadataCollector();
            var runSummaryCollector = new RunSummaryCollector(metadataCollector);
            var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();

            var changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

            var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                sourceApiConnectionDetails,
                fakeSourceRequestHandler,
                targetApiConnectionDetails,
                fakeTargetRequestHandler,
                errorPublisher: errorPublisher,
                runSummaryCollector: runSummaryCollector,
                metadataCollector: metadataCollector,
                changeVersionProcessedWriter: changeVersionProcessedWriter,
                postResourceRateLimiter: rateLimiter);

            Exception caught = null;

            try
            {
                await changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            return new RunResult(caught, errorPublisher, runSummaryCollector, changeVersionProcessedWriter);
        }

        private record RunResult(
            Exception Caught,
            SerilogErrorPublisher ErrorPublisher,
            RunSummaryCollector RunSummaryCollector,
            IChangeVersionProcessedWriter ChangeVersionProcessedWriter);
    }
}
