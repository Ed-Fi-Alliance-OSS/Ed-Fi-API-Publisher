using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using log4net.Appender;
using log4net.Core;
using log4net.Repository;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class IncludeResourcesTests
    {
        [TestFixture]
        public class When_including_publishing_of_a_resource_and_its_dependencies : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;
            private ILoggerRepository _loggerRepository;
            private const string AnyResourcePattern = "/(ed-fi|tpdm)/\\w+";
            
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
                    .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{AnyResourcePattern}", suppliedSourceResources)
                    .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{AnyResourcePattern}/deletes", Array.Empty<object>());

                // -----------------------------------------------------------------

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
               
                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
                
                // Every POST succeeds
                _fakeTargetRequestHandler.PostResource( $"{EdFiApiConstants.DataManagementApiSegment}{AnyResourcePattern}", HttpStatusCode.OK);
                // -----------------------------------------------------------------

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                    include: new []{ "schools" });
            
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

                EdFiApiClient SourceApiClientFactory() =>
                    new EdFiApiClient(
                        "TestSource",
                        sourceApiConnectionDetails,
                        bearerTokenRefreshMinutes: 27,
                        ignoreSslErrors: true,
                        httpClientHandler: new HttpClientHandlerFakeBridge(_fakeSourceRequestHandler));

                EdFiApiClient TargetApiClientFactory() =>
                    new EdFiApiClient(
                        "TestTarget",
                        targetApiConnectionDetails,
                        bearerTokenRefreshMinutes: 27,
                        ignoreSslErrors: true,
                        httpClientHandler: new HttpClientHandlerFakeBridge(_fakeTargetRequestHandler));

                var authorizationFailureHandling = TestHelpers.Configuration.GetAuthorizationFailureHandling();

                // Only include descriptors if our test subject resource is a descriptor (trying to avoid any dependencies to keep things simpler)
                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false; // Shorten test execution time
                
                var configurationStoreSection = null as IConfigurationSection;

                _changeProcessorConfiguration = new ChangeProcessorConfiguration(
                    authorizationFailureHandling,
                    Array.Empty<string>(),
                    sourceApiConnectionDetails,
                    targetApiConnectionDetails,
                    SourceApiClientFactory,
                    TargetApiClientFactory,
                    null,
                    options,
                    configurationStoreSection);

                // Initialize logging
                _loggerRepository = await TestHelpers.InitializeLogging();

                // Create dependencies
                var resourceDependencyProvider = new EdFiV3ApiResourceDependencyProvider();
                var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();
                var errorPublisher = A.Fake<IErrorPublisher>();
                var nodeJsService = A.Fake<INodeJSService>();

                var postResourceBlocksFactory = new PostResourceBlocksFactory(nodeJsService); 

                _changeProcessor = new ChangeProcessor(resourceDependencyProvider, changeVersionProcessedWriter, errorPublisher, postResourceBlocksFactory);
            }

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }

            [TestCase("localEducationAgencies")]
            [TestCase("educationServiceCenters")]
            [TestCase("stateEducationAgencies")]
            [TestCase("postSecondaryInstitutions")] // Results from the tpdm School extension adding a reference
            public void Should_attempt_to_publish_resources_that_are_dependencies_of_the_included_resources(string resourceCollectionName)
            {
                // Should NOT attempt to GET the unincluded resource
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/{resourceCollectionName}",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();

                // Should attempt to POST the unskipped resource
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/{resourceCollectionName}",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
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
            public void Should_reflect_the_processing_as_an_inclusion_with_its_dependencies_in_the_log()
            {
                // Inspect the log entries
                var memoryAppender = _loggerRepository.GetAppenders().OfType<MemoryAppender>().Single();
                var events = memoryAppender.GetEvents();
                
                var initializationEvents = events.Where(e 
                    => e.RenderedMessage.Contains("Including resource '/ed-fi/schools' and its dependencies...")).ToArray();

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