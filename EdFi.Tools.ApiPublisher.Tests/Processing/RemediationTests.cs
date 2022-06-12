using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace EdFi.Tools.ApiPublisher.Tests.Processing;

[TestFixture]
public class RemediationTests
{
    private const string StaffDisciplineIncidentAssociations = "/ed-fi/staffDisciplineIncidentAssociations";

    public class When_a_POST_fails_but_has_JavaScript_extension_module_for_remediation : TestFixtureAsyncBase
    {
        private ChangeProcessor _changeProcessor;
        private ChangeProcessorConfiguration _changeProcessorConfiguration;
        private IFakeHttpRequestHandler _fakeTargetRequestHandler;

        // [TestCase(HttpStatusCode.Forbidden, StaffDisciplineIncidentAssociations, true)]
        HttpStatusCode initialResponseCodeOnPost = HttpStatusCode.Forbidden;
        const string resourcePath = StaffDisciplineIncidentAssociations;
        const bool shouldRetry = true;

        protected override async Task ArrangeAsync()
        {
            // -----------------------------------------------------------------
            //                      Source Requests
            // -----------------------------------------------------------------
            var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

            var suppliedSourceResources = sourceResourceFaker.Generate(1);

            // Prepare the fake source API endpoint
            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()

                // Test-specific mocks
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 1)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{resourcePath}", suppliedSourceResources);

            // -----------------------------------------------------------------

            // -----------------------------------------------------------------
            //                      Target Requests
            // -----------------------------------------------------------------

            _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            if (initialResponseCodeOnPost == HttpStatusCode.OK)
            {
                _fakeTargetRequestHandler.PostResource(
                    $"{EdFiApiConstants.DataManagementApiSegment}{resourcePath}",
                    HttpStatusCode.OK);
            }
            else
            {
                _fakeTargetRequestHandler.PostResource(
                    $"{EdFiApiConstants.DataManagementApiSegment}{resourcePath}",
                    initialResponseCodeOnPost,
                    HttpStatusCode.OK);
            }

            // -----------------------------------------------------------------

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(includeOnly: new[] { resourcePath });

            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            EdFiApiClient SourceApiClientFactory() =>
                new EdFiApiClient(
                    "TestSource",
                    sourceApiConnectionDetails,
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeSourceRequestHandler));

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
            options.IncludeDescriptors = resourcePath.EndsWith("Descriptors");

            var configurationStoreSection = null as IConfigurationSection; //new ConfigurationSection()

            var javaScriptModuleFactory = () => "TestModule";

            _changeProcessorConfiguration = new ChangeProcessorConfiguration(
                authorizationFailureHandling,
                Array.Empty<string>(),
                sourceApiConnectionDetails,
                targetApiConnectionDetails,
                SourceApiClientFactory,
                TargetApiClientFactory,
                javaScriptModuleFactory,
                options,
                configurationStoreSection);

            // Initialize logging
            var loggerRepository = await TestHelpers.InitializeLogging();

            // Create dependencies
            var resourceDependencyProvider = new EdFiV3ApiResourceDependencyProvider();
            var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();
            var errorPublisher = A.Fake<IErrorPublisher>();
            var nodeJsService = A.Fake<INodeJSService>();

            A.CallTo(
                    () => nodeJsService.InvokeFromStringAsync<string>(
                        javaScriptModuleFactory,
                        "RemediationsModule",
                        "/ed-fi/staffDisciplineIncidentAssociations/403",
                        A<object[]>.Ignored,
                        A<CancellationToken>.Ignored))
                .Returns(
                    JsonSerializer.Serialize(
                        new RemediationPlan
                        {
                            modifiedRequestBody = new { type = "modified" },
                            additionalRequests = new[]
                            {
                                new RemediationPlan.RemediationRequest
                                {
                                    resource = "/ed-fi/staffs",
                                    body = new { message = "Staff Request Body" }
                                },
                                new RemediationPlan.RemediationRequest
                                {
                                    resource = "/ed-fi/staffEducationOrganizationAssignmentAssociation",
                                    body = new { message = "Staff EdOrg Assignment Request Body" }
                                }
                            }
                        }));

            var postResourceBlocksFactory = new PostResourceBlocksFactory(nodeJsService);

            _changeProcessor = new ChangeProcessor(
                resourceDependencyProvider,
                changeVersionProcessedWriter,
                errorPublisher,
                postResourceBlocksFactory);
        }

        protected override async Task ActAsync()
        {
            await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
        }

        [Test]
        public void Should_attempt_original_unmodified_request_once()
        {
            // Console.WriteLine(loggerRepository.LoggedContent());

            // Should try POST once with original (unmodified) request body
            A.CallTo(
                    () => _fakeTargetRequestHandler.Post(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{resourcePath}",
                        A<HttpRequestMessage>.That.Matches(req => !HasModifiedRequestBody(req))))
                .MustHaveHappened(1, Times.Exactly);
        }
        
        [Test]
        public void Should_retry_request_even_after_an_otherwise_permanent_failure_with_the_modified_request_provided_by_the_remediation_extension()
        {
            // Should try POST once with modified request body, as directed by the JavaScript extension
            A.CallTo(
                    () => _fakeTargetRequestHandler.Post(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{resourcePath}",
                        A<HttpRequestMessage>.That.Matches(req => HasModifiedRequestBody(req))))
                .MustHaveHappened(1, Times.Exactly);
        }

        private bool HasModifiedRequestBody(HttpRequestMessage requestMessage)
        {
            string content = requestMessage.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();

            var modifiedRequest = JsonConvert.DeserializeObject<dynamic>(content);

            return (modifiedRequest.type == "modified");
        }
        
        [Test]
        public void Should_process_remediation_requests_returned_from_nodejs_service_invocation_with_the_supplied_message_bodies()
        {
            _fakeTargetRequestHandler.ShouldSatisfyAllConditions(
                () =>
                {
                    A.CallTo(
                            () => _fakeTargetRequestHandler.Post(
                                $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/staffs",
                                A<HttpRequestMessage>.That.Matches(
                                    msg => WithMatchingBody(msg, "Staff Request Body"))))
                        .MustHaveHappenedOnceExactly();

                    A.CallTo(
                            () => _fakeTargetRequestHandler.Post(
                                $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/staffEducationOrganizationAssignmentAssociation",
                                A<HttpRequestMessage>.That.Matches(
                                    msg => WithMatchingBody(msg, "Staff EdOrg Assignment Request Body"))))
                        .MustHaveHappenedOnceExactly();
                });
        } 

        private bool WithMatchingBody(HttpRequestMessage msg, string expectedMessage)
        {
            var expectedBody = JObject.Parse(msg?.Content?.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult());

            return expectedBody["message"].Value<string>() == expectedMessage;
        }
    }
}