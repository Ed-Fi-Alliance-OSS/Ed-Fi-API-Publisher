// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Publishes a run from end to end through paths that are not the conventional ones (APIPUB-108). The
    /// resolver's own tests check the value it produces; these check that the value reaches the requests a
    /// run actually sends, on both sides of it.
    /// </summary>
    /// <remarks>
    /// The rest of the suite publishes through <c>data/v3</c>, which is also what the fallback composes, so
    /// a run there cannot tell a resolved path from an assumed one. Both fixtures below use a path the
    /// fallback would never produce, and assert that the conventional one is never requested.
    /// </remarks>
    [TestFixture]
    public class DiscoveryRoutedPublishingTests
    {
        private const string DmsStyleSegment = "data";

        // Change queries needs its own non-conventional path, or the run exercises
        // "changeQueries/v1", which is exactly what the fallback composes.
        private const string DmsStyleChangeQueriesSegment = "changes";

        [TestFixture]
        public class When_both_APIs_declare_a_path_that_is_not_the_conventional_one : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;

            protected override async Task ArrangeAsync()
            {
                var suppliedSourceResources = TestHelpers.GetGenericResourceFaker().Generate(5);

                // Both servers serve, and declare, data management under "data" the way a DMS does.
                _fakeSourceRequestHandler = TestHelpers
                    .GetFakeBaselineSourceApiRequestHandler(
                        dataManagementUrlSegment: DmsStyleSegment,
                        changeQueriesUrlSegment: DmsStyleChangeQueriesSegment)
                    .AvailableChangeVersions(1100)
                    .ResourceCount(responseTotalCountHeader: 1)
                    .GetResourceData($"{DmsStyleSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                    .GetResourceData($"{DmsStyleSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

                _fakeTargetRequestHandler = TestHelpers
                    .GetFakeBaselineTargetApiRequestHandler(
                        dataManagementUrlSegment: DmsStyleSegment,
                        changeQueriesUrlSegment: DmsStyleChangeQueriesSegment);

                _fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(ignoreIsolation: true);
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false;

                TestHelpers.InitializeLogging();

                _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

                _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    _fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    _fakeTargetRequestHandler);

                await Task.Yield();
            }

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }

            [Test]
            public void Should_read_the_source_through_the_declared_path()
            {
                A.CallTo(() => _fakeSourceRequestHandler.Get(
                        A<string>.That.Contains($"/{DmsStyleSegment}/ed-fi/"),
                        A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [Test]
            public void Should_write_to_the_target_through_the_declared_path()
            {
                A.CallTo(() => _fakeTargetRequestHandler.Post(
                        A<string>.That.Contains($"/{DmsStyleSegment}/ed-fi/"),
                        A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [Test]
            public void Should_publish_without_error()
            {
                // Until the POST mock followed the fixture's own segment, nothing here could tell a
                // successful write from a request that simply went unanswered.
                ActualException.ShouldBeNull();
            }

            [Test]
            public void Should_read_change_versions_through_the_declared_path()
            {
                A.CallTo(() => _fakeSourceRequestHandler.Get(
                        $"{MockRequests.SourceApiBaseUrl}/{DmsStyleChangeQueriesSegment}/availableChangeVersions",
                        A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();

                A.CallTo(() => _fakeSourceRequestHandler.Get(
                        $"{MockRequests.SourceApiBaseUrl}/{EdFiApiConstants.ChangeQueriesApiSegment}/availableChangeVersions",
                        A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();
            }

            [Test]
            public void Should_never_request_the_conventional_path_on_either_side()
            {
                // What makes the two assertions above mean anything: the conventional path is what the
                // publisher composed before this change, and what its fallback still composes.
                A.CallTo(() => _fakeSourceRequestHandler.Get(
                        A<string>.That.Contains($"/{EdFiApiConstants.DataManagementApiSegment}/"),
                        A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();

                A.CallTo(() => _fakeTargetRequestHandler.Post(
                        A<string>.That.Contains($"/{EdFiApiConstants.DataManagementApiSegment}/"),
                        A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();
            }
        }

        [TestFixture]
        public class When_a_connection_states_a_path_the_API_does_not_declare : TestFixtureAsyncBase
        {
            private const string StatedSegment = "gateway/data";

            private ChangeProcessor _changeProcessor;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;

            protected override async Task ArrangeAsync()
            {
                var suppliedSourceResources = TestHelpers.GetGenericResourceFaker().Generate(5);

                // Each server serves at the stated path while declaring the conventional one, which is the
                // deployment the override exists for: an API that publishes the address it sits at rather
                // than the address its callers reach it by.
                _fakeSourceRequestHandler = TestHelpers
                    .GetFakeBaselineSourceApiRequestHandler(dataManagementUrlSegment: StatedSegment)
                    .ApiVersionMetadataUrls(
                        apiVersion: "7.3",
                        edfiVersion: "5.2.0",
                        urls: new Dictionary<string, string>
                        {
                            { "dataManagementApi", $"{MockRequests.SourceApiBaseUrl}/{EdFiApiConstants.DataManagementApiSegment}/" },
                            { "changeQueries", $"{MockRequests.SourceApiBaseUrl}/{EdFiApiConstants.ChangeQueriesApiSegment}/" }
                        })
                    .AvailableChangeVersions(1100)
                    .ResourceCount(responseTotalCountHeader: 1)
                    .GetResourceData($"{StatedSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                    .GetResourceData($"{StatedSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

                _fakeTargetRequestHandler = TestHelpers
                    .GetFakeBaselineTargetApiRequestHandler(dataManagementUrlSegment: StatedSegment)
                    .ApiVersionMetadataUrls(
                        apiVersion: "7.3",
                        edfiVersion: "5.2.0",
                        urls: new Dictionary<string, string>
                        {
                            { "dataManagementApi", $"{MockRequests.TargetApiBaseUrl}/{EdFiApiConstants.DataManagementApiSegment}/" },
                            { "changeQueries", $"{MockRequests.TargetApiBaseUrl}/{EdFiApiConstants.ChangeQueriesApiSegment}/" }
                        });

                _fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(ignoreIsolation: true);
                sourceApiConnectionDetails.DataManagementUrlSegment = StatedSegment;

                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();
                targetApiConnectionDetails.DataManagementUrlSegment = StatedSegment;

                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false;

                TestHelpers.InitializeLogging();

                _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

                _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    _fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    _fakeTargetRequestHandler);

                await Task.Yield();
            }

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }

            [Test]
            public void Should_read_the_source_through_the_stated_path()
            {
                A.CallTo(() => _fakeSourceRequestHandler.Get(
                        A<string>.That.Contains($"/{StatedSegment}/ed-fi/"),
                        A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [Test]
            public void Should_write_to_the_target_through_the_stated_path()
            {
                A.CallTo(() => _fakeTargetRequestHandler.Post(
                        A<string>.That.Contains($"/{StatedSegment}/ed-fi/"),
                        A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [Test]
            public void Should_publish_without_error()
            {
                ActualException.ShouldBeNull();
            }

            [Test]
            public void Should_not_follow_what_either_API_declares()
            {
                // Both servers declare the conventional path. A run that followed the document rather than
                // the connection would request it, and these servers answer nothing there.
                A.CallTo(() => _fakeSourceRequestHandler.Get(
                        A<string>.That.Contains($"/{EdFiApiConstants.DataManagementApiSegment}/ed-fi/"),
                        A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();

                A.CallTo(() => _fakeTargetRequestHandler.Post(
                        A<string>.That.Contains($"/{EdFiApiConstants.DataManagementApiSegment}/ed-fi/"),
                        A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();
            }
        }
    }
}
