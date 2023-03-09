using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Bogus;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using EdFi.Tools.ApiPublisher.Tests.Models;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class PostRetryTests
    {
        private const string StateEducationAgencies = "/ed-fi/stateEducationAgencies";
        private const string AddressTypeDescriptors = "/ed-fi/addressTypeDescriptors";
        
        #region Test Cases
        [TestCase(HttpStatusCode.OK, StateEducationAgencies)]
        [TestCase(HttpStatusCode.OK, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.BadRequest, StateEducationAgencies)]
        [TestCase(HttpStatusCode.BadRequest, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.Unauthorized, StateEducationAgencies)]
        [TestCase(HttpStatusCode.Unauthorized, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.PaymentRequired, StateEducationAgencies)]
        [TestCase(HttpStatusCode.PaymentRequired, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.Forbidden, StateEducationAgencies)]
        [TestCase(HttpStatusCode.Forbidden, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.NotFound, StateEducationAgencies)]
        [TestCase(HttpStatusCode.NotFound, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.MethodNotAllowed, StateEducationAgencies)]
        [TestCase(HttpStatusCode.MethodNotAllowed, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.NotAcceptable, StateEducationAgencies)]
        [TestCase(HttpStatusCode.NotAcceptable, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.ProxyAuthenticationRequired, StateEducationAgencies)]
        [TestCase(HttpStatusCode.ProxyAuthenticationRequired, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.RequestTimeout, StateEducationAgencies)]
        [TestCase(HttpStatusCode.RequestTimeout, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.Conflict, StateEducationAgencies, true)]
        [TestCase(HttpStatusCode.Conflict, AddressTypeDescriptors, false)]
        [TestCase(HttpStatusCode.Gone, StateEducationAgencies)]
        [TestCase(HttpStatusCode.Gone, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.LengthRequired, StateEducationAgencies)]
        [TestCase(HttpStatusCode.LengthRequired, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.PreconditionFailed, StateEducationAgencies)]
        [TestCase(HttpStatusCode.PreconditionFailed, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.RequestEntityTooLarge, StateEducationAgencies)]
        [TestCase(HttpStatusCode.RequestEntityTooLarge, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.RequestUriTooLong, StateEducationAgencies)]
        [TestCase(HttpStatusCode.RequestUriTooLong, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.UnsupportedMediaType, StateEducationAgencies)]
        [TestCase(HttpStatusCode.UnsupportedMediaType, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.RequestedRangeNotSatisfiable, StateEducationAgencies)]
        [TestCase(HttpStatusCode.RequestedRangeNotSatisfiable, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.ExpectationFailed, StateEducationAgencies)]
        [TestCase(HttpStatusCode.ExpectationFailed, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.MisdirectedRequest, StateEducationAgencies)]
        [TestCase(HttpStatusCode.MisdirectedRequest, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.UnprocessableEntity, StateEducationAgencies)]
        [TestCase(HttpStatusCode.UnprocessableEntity, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.Locked, StateEducationAgencies)]
        [TestCase(HttpStatusCode.Locked, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.FailedDependency, StateEducationAgencies)]
        [TestCase(HttpStatusCode.FailedDependency, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.UpgradeRequired, StateEducationAgencies)]
        [TestCase(HttpStatusCode.UpgradeRequired, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.PreconditionRequired, StateEducationAgencies)]
        [TestCase(HttpStatusCode.PreconditionRequired, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.TooManyRequests, StateEducationAgencies)]
        [TestCase(HttpStatusCode.TooManyRequests, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.RequestHeaderFieldsTooLarge, StateEducationAgencies)]
        [TestCase(HttpStatusCode.RequestHeaderFieldsTooLarge, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.UnavailableForLegalReasons, StateEducationAgencies)]
        [TestCase(HttpStatusCode.UnavailableForLegalReasons, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.InternalServerError, StateEducationAgencies, true)]
        [TestCase(HttpStatusCode.InternalServerError, AddressTypeDescriptors, true)]
        [TestCase(HttpStatusCode.NotImplemented, StateEducationAgencies)]
        [TestCase(HttpStatusCode.NotImplemented, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.BadGateway, StateEducationAgencies)]
        [TestCase(HttpStatusCode.BadGateway, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.ServiceUnavailable, StateEducationAgencies, true)]
        [TestCase(HttpStatusCode.ServiceUnavailable, AddressTypeDescriptors, true)]
        [TestCase(HttpStatusCode.GatewayTimeout, StateEducationAgencies, true)]
        [TestCase(HttpStatusCode.GatewayTimeout, AddressTypeDescriptors, true)]
        [TestCase(HttpStatusCode.HttpVersionNotSupported, StateEducationAgencies)]
        [TestCase(HttpStatusCode.HttpVersionNotSupported, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.VariantAlsoNegotiates, StateEducationAgencies)]
        [TestCase(HttpStatusCode.VariantAlsoNegotiates, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.InsufficientStorage, StateEducationAgencies)]
        [TestCase(HttpStatusCode.InsufficientStorage, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.LoopDetected, StateEducationAgencies)]
        [TestCase(HttpStatusCode.LoopDetected, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.NotExtended, StateEducationAgencies)]
        [TestCase(HttpStatusCode.NotExtended, AddressTypeDescriptors)]
        [TestCase(HttpStatusCode.NetworkAuthenticationRequired, StateEducationAgencies)]
        [TestCase(HttpStatusCode.NetworkAuthenticationRequired, AddressTypeDescriptors)]
        #endregion
        public async Task When_a_POST_fails_with_certain_errors_should_retry_on_non_permanent_failures(HttpStatusCode initialResponseCodeOnPost, string resourcePath, bool shouldRetry = false)
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
               
            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            if (initialResponseCodeOnPost == HttpStatusCode.OK)
            {
                fakeTargetRequestHandler.PostResource( $"{EdFiApiConstants.DataManagementApiSegment}{resourcePath}", HttpStatusCode.OK);
            }
            else
            {
                fakeTargetRequestHandler.PostResource( $"{EdFiApiConstants.DataManagementApiSegment}{resourcePath}", initialResponseCodeOnPost, HttpStatusCode.OK);
            }
            
            // -----------------------------------------------------------------

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                include: new []{ resourcePath });
            
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

            var changeProcessorConfiguration = new ChangeProcessorConfiguration(
                authorizationFailureHandling,
                Array.Empty<string>(),
                sourceApiConnectionDetails,
                targetApiConnectionDetails,
                SourceApiClientFactory,
                TargetApiClientFactory,
                null,
                options,
                configurationStoreSection);

            // Create dependencies
            var resourceDependencyProvider = new EdFiV3ApiResourceDependencyProvider();
            var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();
            var errorPublisher = A.Fake<IErrorPublisher>();
            var nodeJsService = A.Fake<INodeJSService>();

            var postResourceBlocksFactory = new PostResourceBlocksFactory(nodeJsService); 

            var changeProcessor = new ChangeProcessor(resourceDependencyProvider, changeVersionProcessedWriter, errorPublisher, postResourceBlocksFactory);
            await changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None);

            // Console.WriteLine(loggerRepository.LoggedContent());
            
            // Assert the number of POSTs that should have happened
            A.CallTo(
                    () => fakeTargetRequestHandler.Post(
                        $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{resourcePath}",
                        A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(shouldRetry ? 2 : 1, Times.Exactly);
        }
    }
}