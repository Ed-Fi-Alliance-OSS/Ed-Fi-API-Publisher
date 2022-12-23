using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using log4net.Appender;
using log4net.Core;
using log4net.Repository;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class IncludeOnlyResourcesTests
    {
        [TestFixture]
        public class When_including_publishing_of_a_resource_without_its_dependencies : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;
            private ILoggerRepository _loggerRepository;
            
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
                    includeOnly: new []{ "schools" });
            
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

                // -----------------------------------------------------------------
                //                    Options and Configuration
                // -----------------------------------------------------------------
                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false; // Shorten test execution time
                // -----------------------------------------------------------------
                
                // Initialize logging
                _loggerRepository = await TestHelpers.InitializeLogging();
            
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

            [TestCase("localEducationAgencies")]
            [TestCase("educationServiceCenters")]
            [TestCase("stateEducationAgencies")]
            [TestCase("postSecondaryInstitutions")] // Results from the tpdm School extension adding a reference
            public void Should_NOT_attempt_to_publish_resources_that_are_dependencies_of_the_included_resources(string resourceCollectionName)
            {
                // Should NOT attempt to GET the unincluded resource
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/{resourceCollectionName}",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();

                // Should attempt to POST the unskipped resource
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/{resourceCollectionName}",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();
            }

            [Test]
            public void Should_attempt_to_publish_the_resource_that_is_included()
            {
                // No attempts to GET the skipped resource
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/schools",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();

                // No attempts to POST the skipped resource
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/schools",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }
            
            [Test]
            public void Should_reflect_the_processing_as_an_inclusion_without_its_dependencies_in_the_log()
            {
                // Inspect the log entries
                var memoryAppender = _loggerRepository.GetAppenders().OfType<MemoryAppender>().Single();
                var events = memoryAppender.GetEvents();
                
                var initializationEvents = events.Where(e 
                    => e.RenderedMessage.Contains("Including resource '/ed-fi/schools' without its dependencies...")).ToArray();

                initializationEvents.ShouldSatisfyAllConditions(() =>
                {
                    initializationEvents.ShouldNotBeEmpty();
                    initializationEvents.Select(x => x.Level).ShouldAllBe(x => x == Level.Debug);
                });
            }

            [TestCase("students")]
            [TestCase("studentSchoolAssociations")]
            [TestCase("educationOrganizationNetworkAssociations")]
            [TestCase("educationOrganizationNetworks")]
            public void Should_NOT_attempt_to_publish_resources_that_are_not_dependencies_of_the_included_resource(string resourceCollectionName)
            {
                // Should not attempt to GET the dependent of the skipped resource
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/{resourceCollectionName}",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();

                // Should not attempt to POST the dependent of the skipped resource
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/{resourceCollectionName}",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();
            }
        }
    }
}