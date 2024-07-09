// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using Serilog.Events;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
	[TestFixture]
    public class ExcludeOnlyResourcesTests
    {
        [TestFixture]
        public class When_skipping_publishing_on_a_resource : TestFixtureAsyncBase
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
                //                      Target Requests
                // -----------------------------------------------------------------
               
                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

                // Every POST succeeds
                _fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();
                
                // -----------------------------------------------------------------
                //                  Source/Target Connection Details
                // -----------------------------------------------------------------
                
                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                    excludeOnly: new []{ "schools" });
            
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
				await Task.Yield();
			}

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }

            [Test]
            public void Should_attempt_to_read_and_write_resources_that_are_not_skipped()
            {
                // Should attempt to GET the unskipped resource
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/localEducationAgencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();

                // Should attempt to POST the unskipped resource
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/localEducationAgencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [Test]
            public void Should_not_attempt_to_read_or_write_the_resource_to_be_skipped()
            {
                // No attempts to GET the skipped resource
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/schools",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();

                // No attempts to POST the skipped resource
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/schools",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();
            }

            [Test]
            public void Should_reflect_the_processing_as_an_exclusion_without_affecting_its_dependents_in_the_log()
            {
                LogEvents
                   .Should()
                   .Contain(e => e.MessageTemplate.Text.Contains("Excluding resource '/ed-fi/schools' leaving dependents intact..."))
                   .Which.Level
                   .Should()
                   .Be(LogEventLevel.Debug);
            }

            [Test]
            public void Should_still_attempt_to_publish_resources_that_are_dependent_on_the_skipped_resource()
            {
                // Should attempt to GET the dependent of the skipped resource
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/sessions",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();

                // Should attempt to POST the dependent of the skipped resource
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/sessions",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }
        }
    }
}
