using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.Indexed;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Handling;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Initiators;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Isolation;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Versioning;
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

                var sourceEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(SourceApiClientFactory));
                var targetEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory));

                var authorizationFailureHandling = TestHelpers.Configuration.GetAuthorizationFailureHandling();

                // Only include descriptors if our test subject resource is a descriptor (trying to avoid any dependencies to keep things simpler)
                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false; // Shorten test execution time
                
                var configurationStoreSection = null as IConfigurationSection;

                _changeProcessorConfiguration = new ChangeProcessorConfiguration(
                    authorizationFailureHandling,
                    Array.Empty<string>(),
                    null,
                    options,
                    configurationStoreSection);

                // Initialize logging
                _loggerRepository = await TestHelpers.InitializeLogging();

                // Create dependencies
                var resourceDependencyMetadataProvider = new EdFiOdsApiGraphMLDependencyMetadataProvider(targetEdFiApiClientProvider);
                var resourceDependencyProvider = new ResourceDependencyProvider(resourceDependencyMetadataProvider);
                var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();
                var errorPublisher = A.Fake<IErrorPublisher>();
                var nodeJsService = A.Fake<INodeJSService>();

                var sourceEdFiVersionMetadataProvider = new SourceEdFiOdsApiVersionMetadataProvider(sourceEdFiApiClientProvider);
                var targetEdFiVersionMetadataProvider = new TargetEdFiOdsApiVersionMetadataProvider(targetEdFiApiClientProvider);

                var edFiVersionsChecker = new EdFiVersionsChecker(
                    sourceEdFiVersionMetadataProvider,
                    targetEdFiVersionMetadataProvider);

                var sourceCurrentChangeVersionProvider = new EdFiOdsApiSourceCurrentChangeVersionProvider(sourceEdFiApiClientProvider);
                var sourceIsolationApplicator = new EdFiOdsApiSourceIsolationApplicator(sourceEdFiApiClientProvider);
                var dataSourceCapabilities = new EdFiApiDataSourceCapabilities(sourceEdFiApiClientProvider);
                var publishErrorsBlocksFactory = new PublishErrorsBlocksFactory(errorPublisher);

                var streamingResourceProcessor = new StreamingResourceProcessor(
                    new StreamResourceBlockFactory(
                        new EdFiOdsApiLimitOffsetPagingStreamResourcePageMessageProducer(
                            new EdFiOdsApiDataSourceTotalCountProvider(sourceEdFiApiClientProvider))),
                    new StreamResourcePagesBlockFactory(new ApiStreamResourcePageMessageHandler(sourceEdFiApiClientProvider)),
                    sourceApiConnectionDetails);
                    
                var stageInitiators = A.Fake<IIndex<PublishingStage, IPublishingStageInitiator>>();

                A.CallTo(() => stageInitiators[PublishingStage.KeyChanges])
                    .Returns(
                        new ChangeKeysPublishingStageInitiator(
                            streamingResourceProcessor,
                            new ChangeResourceKeyBlocksFactory(targetEdFiApiClientProvider)));

                A.CallTo(() => stageInitiators[PublishingStage.Upserts])
                    .Returns(
                        new UpsertPublishingStageInitiator(
                            streamingResourceProcessor,
                            new PostResourceBlocksFactory(nodeJsService, sourceEdFiApiClientProvider, targetEdFiApiClientProvider)));

                A.CallTo(() => stageInitiators[PublishingStage.Deletes])
                    .Returns(
                        new DeletePublishingStageInitiator(
                            streamingResourceProcessor,
                            new DeleteResourceBlocksFactory(targetEdFiApiClientProvider)));

                _changeProcessor = new ChangeProcessor(
                    resourceDependencyProvider,
                    changeVersionProcessedWriter,
                    errorPublisher,
                    edFiVersionsChecker,
                    sourceCurrentChangeVersionProvider,
                    sourceApiConnectionDetails,
                    targetApiConnectionDetails,
                    sourceIsolationApplicator,
                    dataSourceCapabilities,
                    publishErrorsBlocksFactory,
                    stageInitiators);
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

                var sourceEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(SourceApiClientFactory));
                var targetEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory));

                var authorizationFailureHandling = TestHelpers.Configuration.GetAuthorizationFailureHandling();

                // Only include descriptors if our test subject resource is a descriptor (trying to avoid any dependencies to keep things simpler)
                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false; // Shorten test execution time
                
                var configurationStoreSection = null as IConfigurationSection;

                _changeProcessorConfiguration = new ChangeProcessorConfiguration(
                    authorizationFailureHandling,
                    Array.Empty<string>(),
                    null,
                    options,
                    configurationStoreSection);

                // Initialize logging
                _loggerRepository = await TestHelpers.InitializeLogging();

                // Create dependencies
                var resourceDependencyMetadataProvider = new EdFiOdsApiGraphMLDependencyMetadataProvider(targetEdFiApiClientProvider);
                var resourceDependencyProvider = new ResourceDependencyProvider(resourceDependencyMetadataProvider);
                var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();
                var errorPublisher = A.Fake<IErrorPublisher>();
                var nodeJsService = A.Fake<INodeJSService>();

                var sourceEdFiVersionMetadataProvider = new SourceEdFiOdsApiVersionMetadataProvider(sourceEdFiApiClientProvider);
                var targetEdFiVersionMetadataProvider = new TargetEdFiOdsApiVersionMetadataProvider(targetEdFiApiClientProvider);

                var edFiVersionsChecker = new EdFiVersionsChecker(
                    sourceEdFiVersionMetadataProvider,
                    targetEdFiVersionMetadataProvider);

                var sourceCurrentChangeVersionProvider = new EdFiOdsApiSourceCurrentChangeVersionProvider(sourceEdFiApiClientProvider);
                var sourceIsolationApplicator = new EdFiOdsApiSourceIsolationApplicator(sourceEdFiApiClientProvider);
                var dataSourceCapabilities = new EdFiApiDataSourceCapabilities(sourceEdFiApiClientProvider);
                var publishErrorsBlocksFactory = new PublishErrorsBlocksFactory(errorPublisher);

                var streamingResourceProcessor = new StreamingResourceProcessor(
                    new StreamResourceBlockFactory(
                        new EdFiOdsApiLimitOffsetPagingStreamResourcePageMessageProducer(
                            new EdFiOdsApiDataSourceTotalCountProvider(sourceEdFiApiClientProvider))),
                    new StreamResourcePagesBlockFactory(new ApiStreamResourcePageMessageHandler(sourceEdFiApiClientProvider)),
                    sourceApiConnectionDetails);
                    
                var stageInitiators = A.Fake<IIndex<PublishingStage, IPublishingStageInitiator>>();

                A.CallTo(() => stageInitiators[PublishingStage.KeyChanges])
                    .Returns(
                        new ChangeKeysPublishingStageInitiator(
                            streamingResourceProcessor,
                            new ChangeResourceKeyBlocksFactory(targetEdFiApiClientProvider)));

                A.CallTo(() => stageInitiators[PublishingStage.Upserts])
                    .Returns(
                        new UpsertPublishingStageInitiator(
                            streamingResourceProcessor,
                            new PostResourceBlocksFactory(nodeJsService, sourceEdFiApiClientProvider, targetEdFiApiClientProvider)));

                A.CallTo(() => stageInitiators[PublishingStage.Deletes])
                    .Returns(
                        new DeletePublishingStageInitiator(
                            streamingResourceProcessor,
                            new DeleteResourceBlocksFactory(targetEdFiApiClientProvider)));

                _changeProcessor = new ChangeProcessor(
                    resourceDependencyProvider,
                    changeVersionProcessedWriter,
                    errorPublisher,
                    edFiVersionsChecker,
                    sourceCurrentChangeVersionProvider,
                    sourceApiConnectionDetails,
                    targetApiConnectionDetails,
                    sourceIsolationApplicator,
                    dataSourceCapabilities,
                    publishErrorsBlocksFactory,
                    stageInitiators);
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

                var sourceEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(SourceApiClientFactory));
                var targetEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory));

                var authorizationFailureHandling = TestHelpers.Configuration.GetAuthorizationFailureHandling();

                // Only include descriptors if our test subject resource is a descriptor (trying to avoid any dependencies to keep things simpler)
                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false; // Shorten test execution time
                
                var configurationStoreSection = null as IConfigurationSection;

                _changeProcessorConfiguration = new ChangeProcessorConfiguration(
                    authorizationFailureHandling,
                    Array.Empty<string>(),
                    null,
                    options,
                    configurationStoreSection);

                // Initialize logging
                _loggerRepository = await TestHelpers.InitializeLogging();

                // Create dependencies
                var resourceDependencyMetadataProvider = new EdFiOdsApiGraphMLDependencyMetadataProvider(targetEdFiApiClientProvider);
                var resourceDependencyProvider = new ResourceDependencyProvider(resourceDependencyMetadataProvider);
                var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();
                var errorPublisher = A.Fake<IErrorPublisher>();
                var nodeJsService = A.Fake<INodeJSService>();

                var sourceEdFiVersionMetadataProvider = new SourceEdFiOdsApiVersionMetadataProvider(sourceEdFiApiClientProvider);
                var targetEdFiVersionMetadataProvider = new TargetEdFiOdsApiVersionMetadataProvider(targetEdFiApiClientProvider);

                var edFiVersionsChecker = new EdFiVersionsChecker(
                    sourceEdFiVersionMetadataProvider,
                    targetEdFiVersionMetadataProvider);

                var sourceCurrentChangeVersionProvider = new EdFiOdsApiSourceCurrentChangeVersionProvider(sourceEdFiApiClientProvider);
                var sourceIsolationApplicator = new EdFiOdsApiSourceIsolationApplicator(sourceEdFiApiClientProvider);
                var dataSourceCapabilities = new EdFiApiDataSourceCapabilities(sourceEdFiApiClientProvider);
                var publishErrorsBlocksFactory = new PublishErrorsBlocksFactory(errorPublisher);

                var streamingResourceProcessor = new StreamingResourceProcessor(
                    new StreamResourceBlockFactory(
                        new EdFiOdsApiLimitOffsetPagingStreamResourcePageMessageProducer(
                            new EdFiOdsApiDataSourceTotalCountProvider(sourceEdFiApiClientProvider))),
                    new StreamResourcePagesBlockFactory(new ApiStreamResourcePageMessageHandler(sourceEdFiApiClientProvider)),
                    sourceApiConnectionDetails);
                    
                var stageInitiators = A.Fake<IIndex<PublishingStage, IPublishingStageInitiator>>();

                A.CallTo(() => stageInitiators[PublishingStage.KeyChanges])
                    .Returns(
                        new ChangeKeysPublishingStageInitiator(
                            streamingResourceProcessor,
                            new ChangeResourceKeyBlocksFactory(targetEdFiApiClientProvider)));

                A.CallTo(() => stageInitiators[PublishingStage.Upserts])
                    .Returns(
                        new UpsertPublishingStageInitiator(
                            streamingResourceProcessor,
                            new PostResourceBlocksFactory(nodeJsService, sourceEdFiApiClientProvider, targetEdFiApiClientProvider)));

                A.CallTo(() => stageInitiators[PublishingStage.Deletes])
                    .Returns(
                        new DeletePublishingStageInitiator(
                            streamingResourceProcessor,
                            new DeleteResourceBlocksFactory(targetEdFiApiClientProvider)));

                _changeProcessor = new ChangeProcessor(
                    resourceDependencyProvider,
                    changeVersionProcessedWriter,
                    errorPublisher,
                    edFiVersionsChecker,
                    sourceCurrentChangeVersionProvider,
                    sourceApiConnectionDetails,
                    targetApiConnectionDetails,
                    sourceIsolationApplicator,
                    dataSourceCapabilities,
                    publishErrorsBlocksFactory,
                    stageInitiators);
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