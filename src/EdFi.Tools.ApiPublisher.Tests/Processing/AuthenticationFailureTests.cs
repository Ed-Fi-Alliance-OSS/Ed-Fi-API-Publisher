// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using EdFi.Tools.ApiPublisher.Tests.Models;
using Bogus;
using FakeItEasy;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Covers the behavior the ticket is ultimately about: a run that cannot authenticate has to end, rather than
    /// continue and report the loss as ordinary per-document or per-page errors.
    /// </summary>
    [TestFixture]
    public class AuthenticationFailureTests
    {
        private const string StateEducationAgencies = "/ed-fi/stateEducationAgencies";
        private const string ResourceWithUpdatableKeys = "/ed-fi/classPeriods";

        [Test]
        public async Task When_the_target_token_cannot_be_obtained_the_run_fails()
        {
            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 1)
                .GetResourceData(
                    $"{EdFiApiConstants.DataManagementApiSegment}{StateEducationAgencies}",
                    TestHelpers.GetGenericResourceFaker().Generate(1));

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            // The target rejects the credentials on every token request
            A.CallTo(() => fakeTargetRequestHandler.Post($"{MockRequests.TargetApiBaseUrl}/oauth/token", A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    () => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{\"error\":\"invalid_client\"}", Encoding.UTF8, "application/json")
                    });

            var caught = await RunAndCaptureAsync(fakeSourceRequestHandler, fakeTargetRequestHandler);

            Assert.That(caught, Is.Not.Null, "A run that cannot authenticate must not complete.");
            Assert.That(
                EdFiApiAuthenticationException.IsRepresentedBy(caught),
                Is.True,
                $"Unexpected exception: {caught}");
        }

        [Test]
        public async Task When_the_source_token_cannot_be_reacquired_the_run_fails_instead_of_reporting_page_errors()
        {
            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 1);

            // The initial token is issued and accepted for the metadata requests; every later token request fails
            A.CallTo(() => fakeSourceRequestHandler.Post($"{MockRequests.SourceApiBaseUrl}/oauth/token", A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => FakeResponse.OK(new { access_token = "source-token" }))
                .Once()
                .Then.ReturnsLazily(
                    () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    });

            // The source rejects that token on the page request, so the client has to re-acquire it and cannot
            string sourceResourceUrl = $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{StateEducationAgencies}";

            A.CallTo(() => fakeSourceRequestHandler.Get(
                    sourceResourceUrl,
                    A<HttpRequestMessage>.That.Matches(request => IsPageRequest(request))))
                .ReturnsLazily(
                    () => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{\"error\":\"invalid_token\"}", Encoding.UTF8, "application/json")
                    });

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
            fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

            // The re-acquisition is retried on a backoff of real seconds, so the run is driven against a fake clock
            var clock = new FakeTimeProvider();

            using (TestCorrelator.CreateContext())
            {
                var caught = await RunAndCaptureAsync(fakeSourceRequestHandler, fakeTargetRequestHandler, timeProvider: clock);

                // The run has to end. The failure surfaces as the processor's own "did not complete successfully"
                // exception rather than the authentication failure itself, because the pipeline keeps only the task
                // statuses and not the exceptions behind them. The authoritative message is the Fatal the API client
                // logs when it gives up; carrying the cause to the top level belongs to the exit code work.
                Assert.That(caught, Is.Not.Null, "A source that cannot authenticate must not let the run complete.");

                // The re-acquisition was retried as often as the policy allows before the client gave up
                A.CallTo(() => fakeSourceRequestHandler.Post($"{MockRequests.SourceApiBaseUrl}/oauth/token", A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened(1 + BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithoutUsableToken, Times.Exactly);

                // The rejected page request was sent once and never replayed
                A.CallTo(() => fakeSourceRequestHandler.Get(
                        sourceResourceUrl,
                        A<HttpRequestMessage>.That.Matches(request => IsPageRequest(request))))
                    .MustHaveHappened(1, Times.Exactly);

                // The one line an operator reading the log needs
                var fatalMessages = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Where(logEvent => logEvent.Level == LogEventLevel.Fatal)
                    .Select(logEvent => logEvent.RenderMessage())
                    .ToList();

                Assert.That(
                    fatalMessages,
                    Has.Exactly(1).Contains("Publishing cannot continue"),
                    $"Fatal log entries: {string.Join(Environment.NewLine, fatalMessages)}");
            }
        }

        [Test]
        public async Task When_the_target_cannot_authenticate_a_key_change_is_not_retried()
        {
            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 1)
                .GetResourceData(@"/data/v3/ed-fi/\w+/keyChanges", GenerateKeyChanges());

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            GivenTheTargetCannotAuthenticate(fakeTargetRequestHandler, ResourceWithUpdatableKeys);

            var caught = await RunAndCaptureAsync(
                fakeSourceRequestHandler,
                fakeTargetRequestHandler,
                includedResource: ResourceWithUpdatableKeys,
                resourcesWithUpdatableKeys: new[] { ResourceWithUpdatableKeys });

            Assert.That(caught, Is.Not.Null, "A key change that cannot be authenticated must not let the run complete.");

            // Attempted once rather than retried MaxRetryAttempts times, because the retry policy excludes an
            // authentication failure
            AssertTargetResourceRequests(fakeTargetRequestHandler, ResourceWithUpdatableKeys, expectedCount: 1);
        }

        [Test]
        public async Task When_the_target_cannot_authenticate_a_delete_is_not_retried()
        {
            var suppliedDeletes = new[]
            {
                new
                {
                    id = Guid.NewGuid().ToString("n"),
                    keyValues = TestHelpers.GetKeyValueFaker().Generate()
                }
            };

            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 1)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}/deletes", suppliedDeletes);

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            GivenTheTargetCannotAuthenticate(fakeTargetRequestHandler, StateEducationAgencies);

            var caught = await RunAndCaptureAsync(fakeSourceRequestHandler, fakeTargetRequestHandler);

            Assert.That(caught, Is.Not.Null, "A delete that cannot be authenticated must not let the run complete.");

            AssertTargetResourceRequests(fakeTargetRequestHandler, StateEducationAgencies, expectedCount: 1);
        }

        private static List<KeyChange<FakeKey>> GenerateKeyChanges()
        {
            var keyValueFaker = TestHelpers.GetKeyValueFaker();
            int changeVersion = 1001;

            var keyChangeFaker = new Faker<KeyChange<FakeKey>>().StrictMode(true)
                .RuleFor(o => o.Id, f => Guid.NewGuid().ToString("n"))
                .RuleFor(o => o.ChangeVersion, f => changeVersion++)
                .Ignore(o => o.OldKeyValues)
                .RuleFor(o => o.OldKeyValuesObject, f => keyValueFaker.Generate())
                .Ignore(o => o.NewKeyValues)
                .RuleFor(o => o.NewKeyValuesObject, f => keyValueFaker.Generate());

            return keyChangeFaker.Generate(1);
        }

        /// <summary>
        /// The request the target makes for the resource fails the way it does once its client has given up on the
        /// token: the handler throws before the request is sent. Matched on the resource URL itself so that the
        /// metadata requests the run needs first are left alone.
        /// </summary>
        private static void GivenTheTargetCannotAuthenticate(
            IFakeHttpRequestHandler fakeTargetRequestHandler,
            string resourcePath)
        {
            A.CallTo(
                    () => fakeTargetRequestHandler.Get(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{resourcePath}",
                        A<HttpRequestMessage>.Ignored))
                .Throws(() => new EdFiApiAuthenticationException("the bearer token could not be obtained"));
        }

        private static void AssertTargetResourceRequests(
            IFakeHttpRequestHandler fakeTargetRequestHandler,
            string resourcePath,
            int expectedCount)
        {
            A.CallTo(
                    () => fakeTargetRequestHandler.Get(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{resourcePath}",
                        A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(expectedCount, Times.Exactly);
        }

        private static bool IsPageRequest(HttpRequestMessage request) =>
            !request.RequestUri.Query.Contains("totalCount", StringComparison.OrdinalIgnoreCase);

        private static async Task<Exception> RunAndCaptureAsync(
            IFakeHttpRequestHandler fakeSourceRequestHandler,
            IFakeHttpRequestHandler fakeTargetRequestHandler,
            string includedResource = StateEducationAgencies,
            string[] resourcesWithUpdatableKeys = null,
            FakeTimeProvider timeProvider = null)
        {
            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                include: new[] { includedResource });

            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;

            TestHelpers.InitializeLogging();

            var changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(
                options,
                resourcesWithUpdatableKeys: resourcesWithUpdatableKeys);

            try
            {
                // Building the processor is inside the capture on purpose: an API client that cannot obtain its
                // initial token fails as soon as anything asks for the client.
                var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    fakeTargetRequestHandler,
                    timeProvider: timeProvider);

                if (timeProvider == null)
                {
                    await changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None);
                }
                else
                {
                    // The processor blocks its calling thread while it waits for the streaming to finish, so the run
                    // is started on another thread to leave this one free to move the fake clock along while the run
                    // waits out the token re-acquisition backoff
                    var run = Task.Run(() => changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None));

                    await timeProvider.AdvanceUntilCompletedAsync(run, BearerTokenRefreshPolicy.InitialRetryDelay);
                }
            }
            catch (Exception ex)
            {
                return ex;
            }

            return null;
        }
    }
}
