// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;

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
        public async Task When_the_source_client_can_no_longer_authenticate_the_run_fails_instead_of_reporting_page_errors()
        {
            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 1);

            // The source client has given up on the token, so every page request fails outright. This must fault the
            // run rather than be recorded as an ordinary page error for each resource still streaming.
            A.CallTo(() => fakeSourceRequestHandler.Get(
                    $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{StateEducationAgencies}",
                    A<HttpRequestMessage>.Ignored))
                .Throws(() => new EdFiApiAuthenticationException("the bearer token could not be obtained"));

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
            fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

            var caught = await RunAndCaptureAsync(fakeSourceRequestHandler, fakeTargetRequestHandler);

            // The run has to end. The failure surfaces as the processor's own "did not complete successfully"
            // exception rather than the authentication failure itself, because the pipeline keeps only the task
            // statuses and not the exceptions behind them. The authoritative message is the Fatal the API client
            // logs when it gives up; carrying the cause to the top level belongs to the exit code work.
            Assert.That(caught, Is.Not.Null, "A source that cannot authenticate must not let the run complete.");
        }

        private static async Task<Exception> RunAndCaptureAsync(
            IFakeHttpRequestHandler fakeSourceRequestHandler,
            IFakeHttpRequestHandler fakeTargetRequestHandler)
        {
            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                include: new[] { StateEducationAgencies });

            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;

            TestHelpers.InitializeLogging();

            var changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

            try
            {
                // Building the processor is inside the capture on purpose: an API client that cannot obtain its
                // initial token fails as soon as anything asks for the client.
                var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    fakeTargetRequestHandler);

                await changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None);
            }
            catch (Exception ex)
            {
                return ex;
            }

            return null;
        }
    }
}
