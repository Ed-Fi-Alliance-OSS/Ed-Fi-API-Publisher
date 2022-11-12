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
    public class ExcludeExtensionResourcesTests
    {
        [TestFixture]
        public class When_excluding_publishing_of_an_extension_resource_and_its_dependents : TestFixtureAsyncBase
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
                    exclude: new []{ "assessments", "/ed-fi/sections", "/tpdm/candidates" });
            
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
                    // sourceApiConnectionDetails,
                    // targetApiConnectionDetails,
                    // SourceApiClientFactory,
                    // TargetApiClientFactory,
                    null,
                    options,
                    configurationStoreSection,
                    new EdFiApiClientProvider(new Lazy<EdFiApiClient>(SourceApiClientFactory)),
                    new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory)));

                // Initialize logging
                _loggerRepository = await TestHelpers.InitializeLogging();

                // Create dependencies
                var resourceDependencyProvider = new ResourceDependencyProvider();
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
                // Inspect the log entries
                var memoryAppender = _loggerRepository.GetAppenders().OfType<MemoryAppender>().Single();
                var events = memoryAppender.GetEvents();
                
                var excludeInitializationEvents = events.Where(e 
                    => e.RenderedMessage.Contains($"Excluding resource '{resourceCollectionUrl}' and its dependents...")).ToArray();

                excludeInitializationEvents.ShouldSatisfyAllConditions(() =>
                {
                    excludeInitializationEvents.ShouldNotBeEmpty();
                    excludeInitializationEvents.Select(x => x.Level).ShouldAllBe(x => x == Level.Debug);
                });
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