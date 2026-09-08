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

            // The rendered table is what an operator actually reads, so the counts have to survive formatting
            string report = RunSummaryFormatter.Format(summary);

            report.ShouldContain(StateEducationAgencies);
            report.ShouldContain("upserts");
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

        private static Task<RunResult> RunWithPostResponsesAsync(params HttpStatusCode[] postResponseCodes)
        {
            return RunWithPostResponsesAsync(postResponseCodes, toleratedItemErrorCount: 0);
        }

        private static async Task<RunResult> RunWithPostResponsesAsync(
            HttpStatusCode[] postResponseCodes,
            int toleratedItemErrorCount,
            bool enableRateLimit = false,
            IRateLimiting<HttpResponseMessage> rateLimiter = null)
        {
            var suppliedSourceResources = TestHelpers.GetGenericResourceFaker().Generate(3);

            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 3)
                .GetResourceData(
                    $"{EdFiApiConstants.DataManagementApiSegment}{StateEducationAgencies}",
                    suppliedSourceResources.ToArray());

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
