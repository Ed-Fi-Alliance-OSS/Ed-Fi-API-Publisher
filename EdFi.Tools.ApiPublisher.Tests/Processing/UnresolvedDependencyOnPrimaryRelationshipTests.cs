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

                var authorizationFailureHandling = TestHelpers.Configuration.GetAuthorizationFailureHandling();

                // Only include descriptors if our test subject resource is a descriptor (trying to avoid any dependencies to keep things simpler)
                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false;
                
                var configurationStoreSection = null as IConfigurationSection; //new ConfigurationSection()

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
                var loggerRepository = await TestHelpers.InitializeLogging();

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