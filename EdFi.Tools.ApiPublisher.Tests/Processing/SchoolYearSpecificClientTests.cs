using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using log4net.Repository;
using Microsoft.Extensions.Configuration;
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
            private ILoggerRepository _loggerRepository;
            private const string AnyResourcePattern = "/(ed-fi|tpdm)/\\w+";

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
                        .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{AnyResourcePattern}", suppliedSourceResources)
                        .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{AnyResourcePattern}/deletes", Array.Empty<object>());

                // -----------------------------------------------------------------

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler(
                    $"{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}");
                
                // Every POST succeeds
                _fakeTargetRequestHandler.PostResource( $"{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}{AnyResourcePattern}", HttpStatusCode.OK);
                // -----------------------------------------------------------------

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();
            
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails(schoolYear: SuppliedSchoolYear);

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
            private ILoggerRepository _loggerRepository;
            private const string AnyResourcePattern = "/(ed-fi|tpdm)/\\w+";

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
                        .GetResourceData($"{dataManagementUrlSegment}{AnyResourcePattern}", suppliedSourceResources)
                        .GetResourceData($"{dataManagementUrlSegment}{AnyResourcePattern}/deletes", Array.Empty<object>());

                // -----------------------------------------------------------------

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
                
                // Every POST succeeds
                _fakeTargetRequestHandler.PostResource( $"{AnyResourcePattern}", HttpStatusCode.OK);
                // -----------------------------------------------------------------

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(schoolYear: SuppliedSchoolYear);
            
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
                // Console.WriteLine(_loggerRepository.LoggedContent());
                
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
            private ILoggerRepository _loggerRepository;
            private const string AnyResourcePattern = "/(ed-fi|tpdm)/\\w+";

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
                        .GetResourceData($"{dataManagementUrlSegment}{AnyResourcePattern}", suppliedSourceResources)
                        .GetResourceData($"{dataManagementUrlSegment}{AnyResourcePattern}/deletes", Array.Empty<object>());

                // -----------------------------------------------------------------

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler(
                    $"{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}");
                
                // Every POST succeeds
                _fakeTargetRequestHandler.PostResource( $"{EdFiApiConstants.DataManagementApiSegment}/{SuppliedSchoolYear}{AnyResourcePattern}", HttpStatusCode.OK);
                // -----------------------------------------------------------------

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(schoolYear: SuppliedSchoolYear);
            
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails(schoolYear: SuppliedSchoolYear);

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