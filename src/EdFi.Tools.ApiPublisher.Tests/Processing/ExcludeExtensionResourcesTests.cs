// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Serilog.Events;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class ExcludeExtensionResourcesTests
    {
        [TestFixture]
        public class When_excluding_publishing_of_an_extension_resource_and_its_dependents : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;

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
               
                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

                // Every POST succeeds
                _fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

                // -----------------------------------------------------------------
                //                  Source/Target Connection Details
                // -----------------------------------------------------------------

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                    exclude: new []{ "assessments", "/ed-fi/sections", "/tpdm/candidates" });
            
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

                // -----------------------------------------------------------------
                //                    Options and Configuration
                // -----------------------------------------------------------------

                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false; // Shorten test execution time
                // -----------------------------------------------------------------

                // Initialize logging
                TestHelpers.InitializeLogging();

                var configurationStoreSection = null as IConfigurationSection;

                // Configuration
                _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

                // Create change processor with dependencies
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

            [TestCase("/ed-fi/localEducationAgencies")]
            [TestCase("/ed-fi/schools")]
            [TestCase("/tpdm/applications")] // This is a dependency (not a dependent) for candidates
            public void Should_attempt_to_publish_resources_that_are_not_excluded(string resourceCollectionUrl)
            {
                // Should attempt to GET the unskipped resource
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{resourceCollectionUrl}",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();

                // Should attempt to POST the unskipped resource
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{resourceCollectionUrl}",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [TestCase("/ed-fi/assessments")]
            [TestCase("/ed-fi/sections")]
            [TestCase("/tpdm/candidates")]
            public void Should_not_attempt_to_publish_the_resource_to_be_skipped(string resourceCollectionUrl)
            {
                // No attempts to GET the skipped resource
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{resourceCollectionUrl}",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();

                // No attempts to POST the skipped resource
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{resourceCollectionUrl}",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();
            }

            [TestCase("/ed-fi/assessments")]
            [TestCase("/ed-fi/sections")]
            [TestCase("/tpdm/candidates")]
            public void Should_reflect_the_processing_as_an_exclusion_with_its_dependents_in_the_log(string resourceCollectionUrl)
            {
                LogEvents
                    .Should()
                    .Contain(e => e.MessageTemplate.Text.Contains($"Excluding resource '{resourceCollectionUrl}' and its dependents..."))
                    .Which.Level
                    .Should()
                    .Be(LogEventLevel.Debug);
            }

            [TestCase("/ed-fi/studentAssessments")] // Depends on assessments
            [TestCase("/ed-fi/studentSectionAssociations")] // Depends on sections
            [TestCase("/tpdm/candidateRelationshipToStaffAssociations")] // Depends on candidates
            public void Should_NOT_attempt_to_publish_resources_that_are_dependent_on_the_excluded_resource(string resourceCollectionUrl)
            {
                // Should not attempt to GET the dependent of the skipped resource
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{resourceCollectionUrl}",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();

                // Should not attempt to POST the dependent of the skipped resource
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{resourceCollectionUrl}",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();
            }
        }
    }
}
