using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.Indexed;
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
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class UnresolvedDependencyOnPrimaryRelationshipTests
    {
        [TestFixture]
        public class When_publishing_a_primary_relationship_resource_with_a_missing_reference : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private string _suppliedSourceLinkHref;

            public class MissingPerson
            {
                [JsonProperty("id")]
                public string Id { get; set; }
                
                [JsonProperty("firstName")]
                public string FirstName { get; set; }
                
                [JsonProperty("lastSurname")]
                public string LastSurname { get; set; }
                
                [JsonProperty("_etag")]
                public string ETag { get; set; }
            }

            const string TestResourcePath = "/ed-fi/studentSchoolAssociations";

            protected override async Task ArrangeAsync()
            {
                // -----------------------------------------------------------------
                //                      Source Requests
                // -----------------------------------------------------------------
                var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();
            
                var suppliedSourceResources = sourceResourceFaker.Generate(1);

                // Prepare the fake source API endpoint
                _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                    // Test-specific mocks
                    .AvailableChangeVersions(1100)
                    .ResourceCount(responseTotalCountHeader: 1)
                    .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestResourcePath}", suppliedSourceResources);
                // -----------------------------------------------------------------

                // Get the path of the missing item from the 'link' in the reference object
                _suppliedSourceLinkHref = suppliedSourceResources.Single().SomeReference.Link.Href;

                // Fake the expected response item  
                var suppliedMissingPerson = new MissingPerson
                {
                    Id = Guid.NewGuid().ToString("n"),
                    FirstName = "Bob",
                    LastSurname = "Jones",
                    ETag = "etagvalue"
                };

                _fakeSourceRequestHandler.GetResourceDataItem(
                    $"{EdFiApiConstants.DataManagementApiSegment}{_suppliedSourceLinkHref}",
                    suppliedMissingPerson);

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
                
                // Override dependencies to a single resource to minimize extraneous noise
                _fakeTargetRequestHandler.Dependencies(TestResourcePath);
                
                _fakeTargetRequestHandler.PostResource( $"{EdFiApiConstants.DataManagementApiSegment}{TestResourcePath}", 
                    (HttpStatusCode.BadRequest, JObject.Parse("{\r\n  \"message\": \"Validation of 'StudentSchoolAssociation' failed.\\r\\n\\tSome reference could not be resolved.\\n\"\r\n}")), 
                    (HttpStatusCode.OK, null));
            
                // -----------------------------------------------------------------

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                    include: new []{ TestResourcePath });
            
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
                options.IncludeDescriptors = false;
                
                var configurationStoreSection = null as IConfigurationSection; //new ConfigurationSection()

                _changeProcessorConfiguration = new ChangeProcessorConfiguration(
                    authorizationFailureHandling,
                    Array.Empty<string>(),
                    null,
                    options,
                    configurationStoreSection);

                // Initialize logging
                var loggerRepository = await TestHelpers.InitializeLogging();

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
            public void Should_attempt_to_post_the_resource_with_the_reference_that_cannot_be_resolved_to_the_target_API_twice()
            {
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{TestResourcePath}",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened(2, Times.Exactly); // Once original attempt, then a second time once the reference has been resolved
            }

            [Test]
            public void Should_attempt_to_get_the_item_for_the_unresolved_reference_from_the_source_API()
            {
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{_suppliedSourceLinkHref}",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }
            
            [Test]
            public void Should_attempt_to_post_the_item_obtained_from_the_source_API_for_the_unresolved_reference_to_the_target_API()
            {
                A.CallTo(
                        () => _fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/students", // This resource path is derived from the authorizationFailureHandling
                            A<HttpRequestMessage>.That.Matches(HasSuppliedStudentInPostRequestBody, "has supplied source item in POST request body")))
                    .MustHaveHappened();
            }
            
            private bool HasSuppliedStudentInPostRequestBody(HttpRequestMessage req)
            {
                string content = req.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();

                var postedObject = JObject.Parse(content);

                postedObject.ShouldSatisfyAllConditions(
                        o => o.ShouldNotBeNull(),
                        o => o.ShouldNotContainKey("id"),
                        o => o.ShouldNotContainKey("_etag"),
                    
                        o => o.ShouldContainKey("firstName"),
                        o => o.ShouldContainKey("lastSurname"),

                        o => o["firstName"]?.Value<string>().ShouldBe("Bob"),
                        o => o["lastSurname"]?.Value<string>().ShouldBe("Jones")
                );

                return true;
            }
        }
    }
}