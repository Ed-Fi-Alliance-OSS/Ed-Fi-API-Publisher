using System;
using System.Collections.Generic;
using System.IO;
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
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing;

[TestFixture]
public class RemediationIntegrationTests
{
    private const string DisciplineActionsResourcePath = "/ed-fi/disciplineActions";
    private const string AssessmentsResourcePath = "/ed-fi/assessments";

    public class When_a_POST_initially_fails_with_a_permanent_failure_but_has_JavaScript_extension_module_backed_by_NodeJs_service_with_remediation_through_additional_requests : TestFixtureAsyncBase
    {
        private ChangeProcessor _changeProcessor;
        private ChangeProcessorConfiguration _changeProcessorConfiguration;
        private IFakeHttpRequestHandler _fakeTargetRequestHandler;

        const HttpStatusCode InitialResponseCodeOnPost = HttpStatusCode.BadRequest;
        const string ResourcePath = DisciplineActionsResourcePath;

        private static string DisciplineActionsItemJson = $@"
    {{
        ""id"": ""28757287fcd74bda8daga49c7d76cdb4"",
        ""responsibilitySchoolReference"": {{
            ""schoolId"": 452,
            ""link"": {{
                ""rel"": ""School"",
                ""href"": ""/ed-fi/schools/28757287fcd74bda8abecd49c7d76cdb4""
            }}
        }},
        ""studentReference"": {{
            ""studentUniqueId"": ""746011"",
            ""link"": {{
                ""rel"": ""Student"",
                ""href"": ""/ed-fi/students/436d7652d2f1487b9498137306c35489""
            }}
        }},
        ""disciplineActionIdentifier"": ""719356"",
        ""disciplineDate"": ""2021-10-26"",
        ""actualDisciplineActionLength"": 3,
        ""iepPlacementMeetingIndicator"": false,
        ""receivedEducationServicesDuringExpulsion"": false,
        ""disciplines"": [
            {{
                ""disciplineDescriptor"": ""uri://ed-fi.org/DisciplineDescriptor#Out of School Suspension""
            }}
        ],
        ""staffs"": [
            {{
                ""staffReference"": {{
                    ""staffUniqueId"": ""11111111"",
                    ""link"": {{
                        ""rel"": ""Staff"",
                        ""href"": ""/ed-fi/staffs/907d910ddfba4c4e9099692211b3c2ee""
                    }}
                }}
            }},
            {{
                ""staffReference"": {{
                    ""staffUniqueId"": ""22222222"",
                    ""link"": {{
                        ""rel"": ""Staff"",
                        ""href"": ""/ed-fi/staffs/907d910ddfba4c4e9099692211b3c2ee""
                    }}
                }}
            }},
            {{
                ""staffReference"": {{
                    ""staffUniqueId"": ""33333333"",
                    ""link"": {{
                        ""rel"": ""Staff"",
                        ""href"": ""/ed-fi/staffs/907d910ddfba4c4e9099692211b3c2ee""
                    }}
                }}
            }}
        ],
        ""studentDisciplineIncidentAssociations"": [
            {{
                ""studentDisciplineIncidentAssociationReference"": {{
                    ""studentUniqueId"": ""746011"",
                    ""incidentIdentifier"": ""442253"",
                    ""schoolId"": 452,
                    ""link"": {{
                        ""rel"": ""StudentDisciplineIncidentAssociation"",
                        ""href"": ""/ed-fi/studentDisciplineIncidentAssociations/a345e36797ae466ca50371f2c10cc687""
                    }}
                }}
            }}
        ],
        ""studentDisciplineIncidentBehaviorAssociations"": []
    }}";

        private static int DisciplineActionsPostResponseStatusCode = 400;

        private const string DisciplineIncidentActionsPostResponseErrorJson = $@"
{{ ""message"": ""Validation of 'DisciplineAction' failed.\\r\\n\\tValidation of 'DisciplineActionStaffs' failed.\\n\\t\\tDisciplineActionStaff[0]: Staff reference could not be resolved.\\n\\t\\tDisciplineActionStaff[2]: Staff reference could not be resolved.\\n""}}";

        private const string AssessmentsResourcePath = "/ed-fi/assessments";

        Func<string> RemediationJavaScriptModuleFactory = () => @$"
         module.exports = {{
             '/ed-fi/disciplineActions/400': async (failureContext) => {{
                 // Parse the request/response data
                 const data = JSON.parse(failureContext.requestBody);
                 const response = JSON.parse(failureContext.responseBody); 

                 // Ensure the error message contains the text associated with the failure we're remediating
                 if (response.message.includes(""Validation of 'DisciplineActionStaffs' failed."") 
                     && response.message.includes(""Staff reference could not be resolved."")) {{
                     
                     const indexRegEx = /DisciplineActionStaff\[(?<Index>[0-9]+)\]/gi;

                     const matches = [...response.message.matchAll(indexRegEx)];

                     return {{ 
                         additionalRequests: 
                             matches.map(m => {{ 
                                 return {{
                                    resource: ""/ed-fi/staffs"", 
                                    body: {{ 
                                         staffUniqueId: `${{data.staffs[m.groups['Index']].staffReference.staffUniqueId}}`, 
                                         firstName: ""Non-DSST Staff"", 
                                         lastSurname: ""Non-DSST Staff"" 
                                     }}
                                 }}
                             }})
                     }};
                 }}

                 return [];
             }}
         }}
         ";
        
        protected override async Task ArrangeAsync()
        {
            // -----------------------------------------------------------------
            //                      Source Requests
            // -----------------------------------------------------------------
            var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();
            var suppliedSourceResources = sourceResourceFaker.Generate(2);

            // Prepare the fake source API endpoint
            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()

                // Test-specific mocks
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 1)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{ResourcePath}", 
                    new [] { JObject.Parse(DisciplineActionsItemJson) })
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{AssessmentsResourcePath}",
                    suppliedSourceResources);

            // -----------------------------------------------------------------

            // -----------------------------------------------------------------
            //                      Target Requests
            // -----------------------------------------------------------------

            _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            // Fake a failure, followed by success
            _fakeTargetRequestHandler.PostResource(
                $"{EdFiApiConstants.DataManagementApiSegment}{ResourcePath}",
                (InitialResponseCodeOnPost, JObject.Parse(DisciplineIncidentActionsPostResponseErrorJson)),
                (HttpStatusCode.OK, null));

            // Fake persistent failure for alternate resource
            _fakeTargetRequestHandler.PostResource(
                $"{EdFiApiConstants.DataManagementApiSegment}{AssessmentsResourcePath}",
                (HttpStatusCode.BadRequest, null),
                (HttpStatusCode.BadRequest, null));

            // -----------------------------------------------------------------

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                includeOnly: new[] { ResourcePath, AssessmentsResourcePath });

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
            // options.IncludeDescriptors = ResourcePath.EndsWith("Descriptors");

            var configurationStoreSection = null as IConfigurationSection; //new ConfigurationSection()

            _changeProcessorConfiguration = new ChangeProcessorConfiguration(
                authorizationFailureHandling,
                Array.Empty<string>(),
                sourceApiConnectionDetails,
                targetApiConnectionDetails,
                SourceApiClientFactory,
                TargetApiClientFactory,
                RemediationJavaScriptModuleFactory,
                options,
                configurationStoreSection);

            // Initialize logging
            var loggerRepository = await TestHelpers.InitializeLogging();

            // Create dependencies
            var resourceDependencyProvider = new EdFiV3ApiResourceDependencyProvider();
            var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();
            var errorPublisher = A.Fake<IErrorPublisher>();
            var nodeJsService = new TestNodeJsService("RemediationWithAdditionalRequests");

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
        public void Should_retry_remediated_requests_even_with_an_otherwise_permanent_failure()
        {
            // Console.WriteLine(loggerRepository.LoggedContent());

            // Assert the number of POSTs that should have happened
            A.CallTo(
                    () => _fakeTargetRequestHandler.Post(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{ResourcePath}",
                        A<HttpRequestMessage>.Ignored))
                .MustHaveHappenedTwiceExactly();
        }

        [Test]
        public void Should_retry_non_remediated_requests_only_for_the_first_item_encountered()
        {
            // Console.WriteLine(loggerRepository.LoggedContent());

            // Assert the number of POSTs that should have happened
            // The assessmentsResource has 2 items, both of which will fail with 403 responses
            // The POST for first item should fail AND be retried because it hasn't been eliminated as NOT having a remediation function yet
            // The POST for the second item should fail after the first attempt, for a total of 3 POST attempts
            A.CallTo(
                    () => _fakeTargetRequestHandler.Post(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{AssessmentsResourcePath}",
                        A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(3, Times.Exactly);
        }

        [Test]
        public void Should_perform_remediation_for_items_with_error_messages()
        {
            _fakeTargetRequestHandler.ShouldSatisfyAllConditions(
                () =>
                {
                    A.CallTo(
                            () => _fakeTargetRequestHandler.Post(
                                $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/staffs",
                                A<HttpRequestMessage>.That.Matches(
                                    msg => WithMatchingStaffUniqueId(msg, "11111111"))))
                        .MustHaveHappenedOnceExactly();

                    A.CallTo(
                            () => _fakeTargetRequestHandler.Post(
                                $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/staffs",
                                A<HttpRequestMessage>.That.Matches(
                                    msg => WithMatchingStaffUniqueId(msg, "33333333"))))
                        .MustHaveHappenedOnceExactly();
                });
        }
        
        [Test]
        public void Should_NOT_perform_remediation_for_items_WITHOUT_error_messages()
        {
            A.CallTo(
                    () => _fakeTargetRequestHandler.Post(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/staffs",
                        A<HttpRequestMessage>.That.Matches(
                            msg => WithMatchingStaffUniqueId(msg, "22222222"))))
                .MustNotHaveHappened();
        }

        private bool WithMatchingStaffUniqueId(HttpRequestMessage msg, string studentUniqueId)
        {
            string? content = msg?.Content?.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();

            if (content == null)
            {
                return false;
            }
            
            var data = JObject.Parse(content);

            return data["staffUniqueId"].Value<string>() == studentUniqueId;
        }
    }

    public class When_a_POST_initially_fails_with_a_permanent_failure_but_has_JavaScript_extension_module_backed_by_NodeJs_service_with_remediation_through_a_modified_request : TestFixtureAsyncBase
    {
        private ChangeProcessor _changeProcessor;
        private ChangeProcessorConfiguration _changeProcessorConfiguration;
        private IFakeHttpRequestHandler _fakeTargetRequestHandler;

        const HttpStatusCode InitialResponseCodeOnPost = HttpStatusCode.BadRequest;
        const string ResourcePath = DisciplineActionsResourcePath;

        private static string DisciplineActionsItemJson = $@"
    {{
        ""id"": ""28757287fcd74bda8daga49c7d76cdb4"",
        ""responsibilitySchoolReference"": {{
            ""schoolId"": 452,
            ""link"": {{
                ""rel"": ""School"",
                ""href"": ""/ed-fi/schools/28757287fcd74bda8abecd49c7d76cdb4""
            }}
        }},
        ""studentReference"": {{
            ""studentUniqueId"": ""746011"",
            ""link"": {{
                ""rel"": ""Student"",
                ""href"": ""/ed-fi/students/436d7652d2f1487b9498137306c35489""
            }}
        }},
        ""disciplineActionIdentifier"": ""719356"",
        ""disciplineDate"": ""2021-10-26"",
        ""actualDisciplineActionLength"": 3,
        ""iepPlacementMeetingIndicator"": false,
        ""receivedEducationServicesDuringExpulsion"": false,
        ""disciplines"": [
            {{
                ""disciplineDescriptor"": ""uri://ed-fi.org/DisciplineDescriptor#Out of School Suspension""
            }}
        ],
        ""staffs"": [
            {{
                ""staffReference"": {{
                    ""staffUniqueId"": ""11111111"",
                    ""link"": {{
                        ""rel"": ""Staff"",
                        ""href"": ""/ed-fi/staffs/907d910ddfba4c4e9099692211b3c2ee""
                    }}
                }}
            }},
            {{
                ""staffReference"": {{
                    ""staffUniqueId"": ""22222222"",
                    ""link"": {{
                        ""rel"": ""Staff"",
                        ""href"": ""/ed-fi/staffs/907d910ddfba4c4e9099692211b3c2ee""
                    }}
                }}
            }},
            {{
                ""staffReference"": {{
                    ""staffUniqueId"": ""33333333"",
                    ""link"": {{
                        ""rel"": ""Staff"",
                        ""href"": ""/ed-fi/staffs/907d910ddfba4c4e9099692211b3c2ee""
                    }}
                }}
            }}
        ],
        ""studentDisciplineIncidentAssociations"": [
            {{
                ""studentDisciplineIncidentAssociationReference"": {{
                    ""studentUniqueId"": ""746011"",
                    ""incidentIdentifier"": ""442253"",
                    ""schoolId"": 452,
                    ""link"": {{
                        ""rel"": ""StudentDisciplineIncidentAssociation"",
                        ""href"": ""/ed-fi/studentDisciplineIncidentAssociations/a345e36797ae466ca50371f2c10cc687""
                    }}
                }}
            }}
        ],
        ""studentDisciplineIncidentBehaviorAssociations"": []
    }}";

        private static int DisciplineActionsPostResponseStatusCode = 400;

        private const string DisciplineIncidentActionsPostResponseErrorJson = $@"
{{ ""message"": ""Validation of 'DisciplineAction' failed.\\r\\n\\tValidation of 'DisciplineActionStaffs' failed.\\n\\t\\tDisciplineActionStaff[0]: Staff reference could not be resolved.\\n\\t\\tDisciplineActionStaff[2]: Staff reference could not be resolved.\\n""}}";

        private const string AssessmentsResourcePath = "/ed-fi/assessments";

        Func<string> RemediationJavaScriptModuleFactory = () => @$"
         module.exports = {{
             '/ed-fi/disciplineActions/400': async (failureContext) => {{
                 // Parse the request/response data
                 const request = JSON.parse(failureContext.requestBody);
                 const response = JSON.parse(failureContext.responseBody); 

                 // Ensure the error message contains the text associated with the failure we're remediating
                 if (response.message.includes(""Validation of 'DisciplineActionStaffs' failed."") 
                     && response.message.includes(""Staff reference could not be resolved."")) {{
                     
                     const indexRegEx = /DisciplineActionStaff\[(?<Index>[0-9]+)\]/gi;

                     const matches = [...response.message.matchAll(indexRegEx)];

                     matches.forEach(m => delete request.staffs[m.groups['Index']]);

                     request.staffs = request.staffs.filter(i => i != null);

                     return {{ modifiedRequestBody: request }};
                 }}

                 return null;
             }}
         }}
         ";
        
        protected override async Task ArrangeAsync()
        {
            // -----------------------------------------------------------------
            //                      Source Requests
            // -----------------------------------------------------------------
            var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();
            var suppliedSourceResources = sourceResourceFaker.Generate(2);

            // Prepare the fake source API endpoint
            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()

                // Test-specific mocks
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 1)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{ResourcePath}", 
                    new [] { JObject.Parse(DisciplineActionsItemJson) })
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{AssessmentsResourcePath}",
                    suppliedSourceResources);

            // -----------------------------------------------------------------

            // -----------------------------------------------------------------
            //                      Target Requests
            // -----------------------------------------------------------------

            _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            // Fake a failure, followed by success
            _fakeTargetRequestHandler.PostResource(
                $"{EdFiApiConstants.DataManagementApiSegment}{ResourcePath}",
                (InitialResponseCodeOnPost, JObject.Parse(DisciplineIncidentActionsPostResponseErrorJson)),
                (HttpStatusCode.OK, null));

            // Fake persistent failure for alternate resource
            _fakeTargetRequestHandler.PostResource(
                $"{EdFiApiConstants.DataManagementApiSegment}{AssessmentsResourcePath}",
                (HttpStatusCode.BadRequest, null),
                (HttpStatusCode.BadRequest, null));

            // -----------------------------------------------------------------

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                includeOnly: new[] { ResourcePath, AssessmentsResourcePath });

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
            // options.IncludeDescriptors = ResourcePath.EndsWith("Descriptors");

            var configurationStoreSection = null as IConfigurationSection; //new ConfigurationSection()

            _changeProcessorConfiguration = new ChangeProcessorConfiguration(
                authorizationFailureHandling,
                Array.Empty<string>(),
                sourceApiConnectionDetails,
                targetApiConnectionDetails,
                SourceApiClientFactory,
                TargetApiClientFactory,
                RemediationJavaScriptModuleFactory,
                options,
                configurationStoreSection);

            // Initialize logging
            var loggerRepository = await TestHelpers.InitializeLogging();

            // Create dependencies
            var resourceDependencyProvider = new EdFiV3ApiResourceDependencyProvider();
            var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();
            var errorPublisher = A.Fake<IErrorPublisher>();
            var nodeJsService = new TestNodeJsService("RemediationWithModifiedRequest");

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
        public void Should_retry_remediated_requests_even_with_an_otherwise_permanent_failure()
        {
            // Console.WriteLine(loggerRepository.LoggedContent());

            // Assert the number of POSTs that should have happened
            A.CallTo(
                    () => _fakeTargetRequestHandler.Post(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{ResourcePath}",
                        A<HttpRequestMessage>.Ignored))
                .MustHaveHappenedTwiceExactly();
        }

        [Test]
        public void Should_retry_non_remediated_requests_only_for_the_first_item_encountered()
        {
            // Console.WriteLine(loggerRepository.LoggedContent());

            // Assert the number of POSTs that should have happened
            // The assessmentsResource has 2 items, both of which will fail with 403 responses
            // The POST for first item should fail AND be retried because it hasn't been eliminated as NOT having a remediation function yet
            // The POST for the second item should fail after the first attempt, for a total of 3 POST attempts
            A.CallTo(
                    () => _fakeTargetRequestHandler.Post(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{AssessmentsResourcePath}",
                        A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(3, Times.Exactly);
        }

        [Test]
        public void Should_perform_remediation_for_items_with_error_messages()
        {
            // First and last entries should have been removed, second entry retained
            A.CallTo(
                    () => _fakeTargetRequestHandler.Post(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{ResourcePath}",
                        A<HttpRequestMessage>.That.Matches(
                            msg => WithExactEntriesForStaffUniqueIds(msg, "22222222"))))
                .MustHaveHappenedOnceExactly();
        }

        private bool WithExactEntriesForStaffUniqueIds(HttpRequestMessage msg, params string[] requiredStaffUniqueIds)
        {
            string? content = msg?.Content?.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();

            if (content == null)
            {
                return false;
            }

            var data = JsonConvert.DeserializeObject<dynamic>(content);

            // Check the length of the staffs array
            if (requiredStaffUniqueIds.Length != data.staffs.Count)
            {
                return false;
            }
            
            return ((IEnumerable<dynamic>) data.staffs)
                .All(s => requiredStaffUniqueIds.Contains((string) s.staffReference.staffUniqueId));
        }
    }

    public class TestNodeJsService : INodeJSService
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(TestNodeJsService));

        private readonly string _testCacheIdentifier;

        public TestNodeJsService(string testCacheIdentifier)
        {
            _testCacheIdentifier = testCacheIdentifier;
        }
        
        public Task<T?> InvokeFromStringAsync<T>(
            Func<string> moduleFactory,
            string cacheIdentifier,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            _logger.Debug($"InvokeFromStringAsync: cacheIdentifier: '{cacheIdentifier}', _testCacheIdentifier: '{_testCacheIdentifier}', exportName: '{exportName}'");

            return StaticNodeJSService.InvokeFromStringAsync<T>(
                moduleFactory,
                _testCacheIdentifier, // Use the cache identifier associated with the test instance to avoid script conflicts
                exportName,
                args,
                cancellationToken);
        }

        #region Not Implemented Members

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public Task<T?> InvokeFromFileAsync<T>(
            string modulePath,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public Task InvokeFromFileAsync(
            string modulePath,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public Task InvokeFromStringAsync(
            string moduleString,
            string? cacheIdentifier = null,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public Task<T?> InvokeFromStringAsync<T>(
            string moduleString,
            string? cacheIdentifier = null,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public Task InvokeFromStringAsync(
            Func<string> moduleFactory,
            string cacheIdentifier,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public Task<T?> InvokeFromStreamAsync<T>(
            Stream moduleStream,
            string? cacheIdentifier = null,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public Task InvokeFromStreamAsync(
            Stream moduleStream,
            string? cacheIdentifier = null,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public Task<T?> InvokeFromStreamAsync<T>(
            Func<Stream> moduleFactory,
            string cacheIdentifier,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public Task InvokeFromStreamAsync(
            Func<Stream> moduleFactory,
            string cacheIdentifier,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public Task<(bool, T?)> TryInvokeFromCacheAsync<T>(
            string cacheIdentifier,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public Task<bool> TryInvokeFromCacheAsync(
            string cacheIdentifier,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public void MoveToNewProcess()
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}