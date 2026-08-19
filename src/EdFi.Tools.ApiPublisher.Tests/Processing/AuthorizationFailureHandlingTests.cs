// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class AuthorizationFailureHandlingTests
    {
        private const string StateEducationAgencies = "/ed-fi/stateEducationAgencies";

        [Test]
        public async Task Entries_with_paths_unknown_to_the_dependency_graph_should_be_skipped_instead_of_crashing()
        {
            TestHelpers.InitializeLogging();

            const int TotalItems = 10;

            var suppliedPageOfResources = TestHelpers.GetGenericResourceFaker().Generate(TotalItems);

            // Full-catalog publish (no include filter): the inclusion filters strip retry entries for
            // non-included paths, so only a full publish reproduces the field crash
            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: TotalItems)
                .GetResourceData(@"/data/v3/ed-fi/\w+", suppliedPageOfResources);

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            A.CallTo(() => fakeTargetRequestHandler.Post(A<string>.Ignored, A<HttpRequestMessage>.Ignored))
                .Returns(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();

            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;

            // An entry whose path/prerequisites do not exist in the source's dependency graph -- e.g. the
            // formerly shipped "/ed-fi/parents" entry against a Data Standard 5.x source (where the resource
            // is "/ed-fi/contacts") -- previously crashed the run with a KeyNotFoundException when the retry
            // node's prerequisite was looked up for streaming. It must be skipped with a warning instead.
            var authorizationFailureHandling = new[]
            {
                new AuthorizationFailureHandling
                {
                    Path = "/ed-fi/notARealResource",
                    UpdatePrerequisitePaths = new[] { "/ed-fi/alsoNotARealResource" }
                }
            };

            var changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(
                options,
                authorizationFailureHandling: authorizationFailureHandling);

            var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                sourceApiConnectionDetails,
                fakeSourceRequestHandler,
                targetApiConnectionDetails,
                fakeTargetRequestHandler);

            // Task.Run: the fake HTTP handlers complete synchronously, so the blocking publish loop would
            // otherwise run entirely on this thread
            await Task.Run(
                    () => changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(60));

            // The known resource must still publish normally
            A.CallTo(
                    () => fakeTargetRequestHandler.Post(
                        A<string>.That.Matches(url => url.EndsWith(StateEducationAgencies)),
                        A<HttpRequestMessage>.Ignored))
                .MustHaveHappened();
        }
    }
}
