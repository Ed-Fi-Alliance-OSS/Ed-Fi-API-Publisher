using System;
using System.IO;
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
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing;

public class RemediationTests
{
    private const string StaffDisciplineIncidentAssociations = "/ed-fi/staffDisciplineIncidentAssociations";
    private const string StudentSectionAssociations = "/ed-fi/studentSectionAssociations";

    [TestCase(HttpStatusCode.Forbidden, StaffDisciplineIncidentAssociations, true)]
    public async Task
        When_a_POST_fails_but_has_JavaScript_extension_module_for_remediation_should_retry_even_on_otherwise_permanent_failures(
            HttpStatusCode initialResponseCodeOnPost,
            string resourcePath,
            bool shouldRetry = false)
    {
        //             string javaScriptSource = @$"
        // module.exports = {{
        //     '/ed-fi/staffDisciplineIncidentAssociations/403': async (failureContext) => {{
        //         const result = {{ 
        //             requests: [
        //                 {{ 
        //                     resource: '/ed-fi/staffs',
        //                     body: {{ staffUniqueId: 'ABC123', firstName: 'Bob', lastSurname: 'Jones' }} 
        //                 }},
        //                 {{ 
        //                     resource: '/ed-fi/staffDisciplineIncidentAssociations',
        //                     body: {{ this: 'Thing1', that: 'Thing2', theOther: 'Another' }} 
        //                 }}
        //             ]
        //         }};
        //         return result;
        //     }}
        // }}
        // ";

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

        var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

        if (initialResponseCodeOnPost == HttpStatusCode.OK)
        {
            fakeTargetRequestHandler.PostResource(
                $"{EdFiApiConstants.DataManagementApiSegment}{resourcePath}",
                HttpStatusCode.OK);
        }
        else
        {
            fakeTargetRequestHandler.PostResource(
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
                httpClientHandler: new HttpClientHandlerFakeBridge(fakeTargetRequestHandler));

        var authorizationFailureHandling = TestHelpers.Configuration.GetAuthorizationFailureHandling();

        // Only include descriptors if our test subject resource is a descriptor (trying to avoid any dependencies to keep things simpler)
        var options = TestHelpers.GetOptions();
        options.IncludeDescriptors = resourcePath.EndsWith("Descriptors");

        var configurationStoreSection = null as IConfigurationSection; //new ConfigurationSection()

        var javaScriptModuleFactory = () => "TestModule";

        var changeProcessorConfiguration = new ChangeProcessorConfiguration(
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
                () => nodeJsService.InvokeFromStringAsync<RemediationPlan>(
                    javaScriptModuleFactory,
                    "RemediationsModule",
                    "/ed-fi/staffDisciplineIncidentAssociations/403",
                    A<object[]>.Ignored,
                    A<CancellationToken>.Ignored))
            .Returns(
                new RemediationPlan
                {
                    requests = new[]
                    {
                        new RemediationPlan.RemediationRequest
                        {
                            resource = "/ed-fi/staffs",
                            body = "Staff Request Body"
                        },
                        new RemediationPlan.RemediationRequest
                        {
                            resource = "/ed-fi/staffEducationOrganizationAssignmentAssociation",
                            body = "Staff EdOrg Assignment Request Body"
                        }
                    }
                });

        // That.Matches(
        //         fc =>
        //         {
        //             fc.resourceUrl == "/ed-fi/staffDisciplineIncidentAssociations"
        //                 && fc.
        //         })))

        var postResourceBlocksFactory = new PostResourceBlocksFactory(nodeJsService);

        var changeProcessor = new ChangeProcessor(
            resourceDependencyProvider,
            changeVersionProcessedWriter,
            errorPublisher,
            postResourceBlocksFactory);

        await changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None);

        // Console.WriteLine(loggerRepository.LoggedContent());

        // Assert the number of POSTs that should have happened
        fakeTargetRequestHandler.ShouldSatisfyAllConditions(
            () =>
            {
                A.CallTo(
                        () => fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{resourcePath}",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened(shouldRetry ? 2 : 1, Times.Exactly);

                A.CallTo(
                        () => fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/staffs",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappenedOnceExactly();

                A.CallTo(
                        () => fakeTargetRequestHandler.Post(
                            $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}/ed-fi/staffEducationOrganizationAssignmentAssociation",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappenedOnceExactly();
            });
    }

    public class TestNodeJsService : INodeJSService
    {
        public Task<T?> InvokeFromStringAsync<T>(
            Func<string> moduleFactory,
            string cacheIdentifier,
            string? exportName = null,
            object?[]? args = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            return StaticNodeJSService.InvokeFromStringAsync<T>(
                moduleFactory,
                cacheIdentifier,
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