// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class SchoolYearSpecificClientTests
    {
        [TestFixture]
        public class When_publishing_resources_to_SchoolYear_specific_deployment : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;
            
            private const int SuppliedSchoolYear = MockRequests.SchoolYear;

            protected override async Task ArrangeAsync()
            {
                // -----------------------------------------------------------------
                //                      Source Requests
                // -----------------------------------------------------------------
                var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

                var suppliedSourceResources = sourceResourceFaker.Generate(5);

                // Prepare the fake source API endpoint
                _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()

                        // Test-specific mocks
                        .AvailableChangeVersions(1100)
                        .ResourceCount(responseTotalCountHeader: 1)
                        .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                        .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

                // -----------------------------------------------------------------

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler(
                    $"{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}");

                // Every POST succeeds
                _fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

                // -----------------------------------------------------------------
                //                  Source/Target Connection Details
                // -----------------------------------------------------------------

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails(schoolYear: SuppliedSchoolYear);

                // -----------------------------------------------------------------
                //                    Options and Configuration
                // -----------------------------------------------------------------
                
                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false; // Shorten test execution time

                // -----------------------------------------------------------------

                // Initialize logging
                TestHelpers.InitializeLogging();

                // Configuration
                _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

                // Create change processor with dependencies
                _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    _fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    _fakeTargetRequestHandler);
            }

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }

            [Test]
            public void Should_get_dependencies_from_the_target_API_using_the_schoolyear_specific_URL()
            {
                A.CallTo(
                        () => _fakeTargetRequestHandler.Get(
                            $"{MockRequests.TargetApiBaseUrl}/metadata/{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}/dependencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }
            
            [Test]
            public void Should_attempt_to_read_resources_WITHOUT_schoolyear_in_the_source_path()
            {
                // Source should NOT include the school year
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/localEducationAgencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [Test]
            public void Should_attempt_to_write_resources_WITH_schoolyear_in_the_target_path()
            {
                // Target should include school year
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.SchoolYearSpecificDataManagementPath}/ed-fi/localEducationAgencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }
        }

        [TestFixture]
        public class When_publishing_resources_from_SchoolYear_specific_deployment : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;

            private const int SuppliedSchoolYear = MockRequests.SchoolYear;

            protected override async Task ArrangeAsync()
            {
                // -----------------------------------------------------------------
                //                      Source Requests
                // -----------------------------------------------------------------
                var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

                var suppliedSourceResources = sourceResourceFaker.Generate(5);

                // Prepare the fake source API endpoint
                string dataManagementUrlSegment = $"{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}";

                _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler(
                        dataManagementUrlSegment,
                        $"{EdFiApiConstants.ChangeQueriesApiSegment}/{SuppliedSchoolYear}")

                        // Test-specific mocks
                        .AvailableChangeVersions(1100)
                        .ResourceCount(responseTotalCountHeader: 1)
                        .GetResourceData($"{dataManagementUrlSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                        .GetResourceData($"{dataManagementUrlSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
                
                // Every POST succeeds
                _fakeTargetRequestHandler.PostResource( $"{TestHelpers.AnyResourcePattern}", HttpStatusCode.OK);
                // -----------------------------------------------------------------

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(schoolYear: SuppliedSchoolYear);
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();
                
                // -----------------------------------------------------------------
                //                    Options and Configuration
                // -----------------------------------------------------------------

                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false; // Shorten test execution time

                // -----------------------------------------------------------------

                // Initialize logging
                TestHelpers.InitializeLogging();

                // Configuration
                _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

                // Create change processor with dependencies
                _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    _fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    _fakeTargetRequestHandler);
            }

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }

            [Test]
            public void Should_get_dependencies_from_the_target_API_using_the_default_URL()
            {
                A.CallTo(
                        () => _fakeTargetRequestHandler.Get(
                            $"{MockRequests.TargetApiBaseUrl}/metadata/{EdFiApiConstants.DataManagementApiSegment}/dependencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }
            
            [Test]
            public void Should_attempt_to_read_resources_WITH_schoolyear_in_the_source_path()
            {
                // Source should NOT include the school year
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.SchoolYearSpecificDataManagementPath}/ed-fi/localEducationAgencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [Test]
            public void Should_attempt_to_write_resources_WITHOUT_schoolyear_in_the_target_path()
            {
                // Target should include school year
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/localEducationAgencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }
        }

        [TestFixture]
        public class When_publishing_resources_to_and_from_SchoolYear_specific_deployments : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;

            private const int SuppliedSchoolYear = MockRequests.SchoolYear;

            protected override async Task ArrangeAsync()
            {
                // -----------------------------------------------------------------
                //                      Source Requests
                // -----------------------------------------------------------------
                var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

                var suppliedSourceResources = sourceResourceFaker.Generate(5);

                // Prepare the fake source API endpoint
                string dataManagementUrlSegment = $"{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}";

                _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler(
                        dataManagementUrlSegment,
                        $"{EdFiApiConstants.ChangeQueriesApiSegment}/{SuppliedSchoolYear}")

                        // Test-specific mocks
                        .AvailableChangeVersions(1100)
                        .ResourceCount(responseTotalCountHeader: 1)
                        .GetResourceData($"{dataManagementUrlSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                        .GetResourceData($"{dataManagementUrlSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler(
                    $"{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}");
                
                // Every POST succeeds
                _fakeTargetRequestHandler.PostResource( $"{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}{TestHelpers.AnyResourcePattern}", HttpStatusCode.OK);

                // -----------------------------------------------------------------
                //                  Source/Target Connection Details
                // -----------------------------------------------------------------

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(schoolYear: SuppliedSchoolYear);
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails(schoolYear: SuppliedSchoolYear);

                // -----------------------------------------------------------------
                //                    Options and Configuration
                // -----------------------------------------------------------------

                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false; // Shorten test execution time

                // -----------------------------------------------------------------

                // Initialize logging
                TestHelpers.InitializeLogging();

                // Configuration
                _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

                // Create change processor with dependencies
                _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    _fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    _fakeTargetRequestHandler);

            }

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }

            [Test]
            public void Should_get_dependencies_from_the_target_API_using_the_schoolyear_specific_URL()
            {
                A.CallTo(
                        () => _fakeTargetRequestHandler.Get(
                            $"{MockRequests.TargetApiBaseUrl}/metadata/{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}/dependencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }
            
            [Test]
            public void Should_attempt_to_read_resources_WITH_schoolyear_in_the_source_path()
            {
                // Source should NOT include the school year
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.SchoolYearSpecificDataManagementPath}/ed-fi/localEducationAgencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [Test]
            public void Should_attempt_to_write_resources_WITH_schoolyear_in_the_target_path()
            {
                // Target should include school year
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.SchoolYearSpecificDataManagementPath}/ed-fi/localEducationAgencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }
        }
    }
}
