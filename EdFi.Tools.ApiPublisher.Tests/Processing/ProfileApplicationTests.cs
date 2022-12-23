// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.Indexed;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;
using EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Dependencies;
using EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Capabilities;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Counting;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Isolation;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageHandlers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Versioning;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Initiators;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Finalization;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using log4net.Repository;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.Processing;

[TestFixture]
public class ProfileApplicationTests
{
    [TestFixture]
    public class When_applying_profiles_to_source_and_target_connections : TestFixtureAsyncBase
    {
        private ChangeProcessor _changeProcessor;
        private IFakeHttpRequestHandler _fakeTargetRequestHandler;
        private IFakeHttpRequestHandler _fakeSourceRequestHandler;
        private ChangeProcessorConfiguration _changeProcessorConfiguration;
        private ILoggerRepository _loggerRepository;
        private const string AnyResourcePattern = "/ed-fi/\\w+";
        private const string TestWritableProfileName = "Unit-Test-Target-Profile";
        private const string TestReadableProfileName = "Unit-Test-Source-Profile";

        protected override async Task ArrangeAsync()
        {
            // -----------------------------------------------------------------
            //                      Source Requests
            // -----------------------------------------------------------------
            var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

            var suppliedSourceResources = sourceResourceFaker.Generate(3);

            // Prepare the fake source API endpoint
            _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                // Test-specific mocks
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 3)
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

            // Configure API connection details for the test
            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                includeOnly: new[]
                {
                    "students",
                    "academicSubjectDescriptors"
                },
                profileName: TestReadableProfileName);

            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails(profileName: TestWritableProfileName);

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

            // Initialize options
            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = true; // Allow descriptors to be included

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
            var resourceDependencyMetadataProvider = new EdFiApiGraphMLDependencyMetadataProvider(targetEdFiApiClientProvider);
            var resourceDependencyProvider = new ResourceDependencyProvider(resourceDependencyMetadataProvider);
            var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();
            var errorPublisher = A.Fake<IErrorPublisher>();
            var nodeJsService = A.Fake<INodeJSService>();

            var sourceEdFiVersionMetadataProvider = new SourceEdFiApiVersionMetadataProvider(sourceEdFiApiClientProvider);
            var targetEdFiVersionMetadataProvider = new TargetEdFiApiVersionMetadataProvider(targetEdFiApiClientProvider);

            var edFiVersionsChecker = new EdFiVersionsChecker(
                sourceEdFiVersionMetadataProvider,
                targetEdFiVersionMetadataProvider);

            var sourceCurrentChangeVersionProvider = new EdFiApiSourceCurrentChangeVersionProvider(sourceEdFiApiClientProvider);
            var sourceIsolationApplicator = new EdFiApiSourceIsolationApplicator(sourceEdFiApiClientProvider);
            var dataSourceCapabilities = new EdFiApiSourceCapabilities(sourceEdFiApiClientProvider);
            var publishErrorsBlocksFactory = new PublishErrorsBlocksFactory(errorPublisher);

            var streamingResourceProcessor = new StreamingResourceProcessor(
                new StreamResourceBlockFactory(
                    new EdFiApiLimitOffsetPagingStreamResourcePageMessageProducer(
                        new EdFiApiSourceTotalCountProvider(sourceEdFiApiClientProvider))),
                new StreamResourcePagesBlockFactory(new EdFiApiStreamResourcePageMessageHandler(sourceEdFiApiClientProvider)),
                sourceApiConnectionDetails);
                
            var stageInitiators = A.Fake<IIndex<PublishingStage, IPublishingStageInitiator>>();

            A.CallTo(() => stageInitiators[PublishingStage.KeyChanges])
                .Returns(
                    new KeyChangePublishingStageInitiator(
                        streamingResourceProcessor,
                        new ChangeResourceKeyProcessingBlocksFactory(targetEdFiApiClientProvider)));

            A.CallTo(() => stageInitiators[PublishingStage.Upserts])
                .Returns(
                    new UpsertPublishingStageInitiator(
                        streamingResourceProcessor,
                        new PostResourceProcessingBlocksFactory(nodeJsService, targetEdFiApiClientProvider, 
                            sourceApiConnectionDetails, dataSourceCapabilities, 
                            new ApiSourceResourceItemProvider(sourceEdFiApiClientProvider, options))));
            
            A.CallTo(() => stageInitiators[PublishingStage.Deletes])
                .Returns(
                    new DeletePublishingStageInitiator(
                        streamingResourceProcessor,
                        new DeleteResourceProcessingBlocksFactory(targetEdFiApiClientProvider)));

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
                stageInitiators,
                Array.Empty<IFinalizationActivity>());
        }

        protected override async Task ActAsync()
        {
            await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
        }

        [Test]
        public void Should_NOT_apply_profile_content_types_to_descriptors_GET_requests()
        {
            A.CallTo(() => _fakeSourceRequestHandler.Get(
                    $@"{MockRequests.SourceApiBaseUrl}/{_fakeSourceRequestHandler.DataManagementUrlSegment}/ed-fi/academicSubjectDescriptors",
                    A<HttpRequestMessage>.That.Matches(msg => DoesNotUseProfileContentType(msg))))
                .MustHaveHappenedTwiceExactly(); // Once for count, once for data
        }
        
        [Test]
        public void Should_NOT_apply_profile_content_types_to_descriptors_POST_requests()
        {
            A.CallTo(() => _fakeTargetRequestHandler.Post(
                    $@"{MockRequests.TargetApiBaseUrl}/{_fakeTargetRequestHandler.DataManagementUrlSegment}/ed-fi/academicSubjectDescriptors",
                    A<HttpRequestMessage>.That.Matches(msg => DoesNotUseProfileContentType(msg))))
                .MustHaveHappened(3, Times.Exactly);
        }
        
        [Test]
        public void Should_apply_readable_profile_content_type_to_count_requests()
        {
            A.CallTo(() => _fakeSourceRequestHandler.Get(
                    $@"{MockRequests.SourceApiBaseUrl}/{_fakeSourceRequestHandler.DataManagementUrlSegment}/ed-fi/students",
                    A<HttpRequestMessage>.That.Matches(msg => UsesReadableContentType(msg) && QueryStringHasTotalCount(msg.RequestUri))))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void Should_apply_readable_profile_content_type_to_all_GET_requests()
        {
            A.CallTo(() => _fakeSourceRequestHandler.Get(
                    $@"{MockRequests.SourceApiBaseUrl}/{_fakeSourceRequestHandler.DataManagementUrlSegment}/ed-fi/students",
                    A<HttpRequestMessage>.That.Matches(msg => UsesReadableContentType(msg) && !QueryStringHasTotalCount(msg.RequestUri))))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void Should_apply_writable_profile_content_type_to_all_POST_requests()
        {
            A.CallTo(() => _fakeTargetRequestHandler.Post(
                    $@"{MockRequests.TargetApiBaseUrl}/{_fakeTargetRequestHandler.DataManagementUrlSegment}/ed-fi/students",
                    A<HttpRequestMessage>.That.Matches(msg => UsesWritableContentType(msg))))
                .MustHaveHappened(3, Times.Exactly);
        }

        [Test]
        public void Should_NOT_apply_profile_content_type_to_any_deletes_requests()
        {
            A.CallTo(() => _fakeSourceRequestHandler.Get(
                    $@"{MockRequests.SourceApiBaseUrl}/{_fakeSourceRequestHandler.DataManagementUrlSegment}/ed-fi/students/deletes",
                    A<HttpRequestMessage>.That.Matches(msg => DoesNotUseProfileContentType(msg))))
                .MustHaveHappenedOnceOrMore();
        }

        private static bool DoesNotUseProfileContentType(HttpRequestMessage msg)
        {
            return !msg.Headers.Accept.ToString().StartsWith("application/vnd.ed-fi.");
        }

        private bool QueryStringHasTotalCount(Uri? msgRequestUri)
        {
            return msgRequestUri?.ParseQueryString().AllKeys.Contains("totalCount", StringComparer.OrdinalIgnoreCase) ?? false;
        }
        
        private bool UsesReadableContentType(HttpRequestMessage requestMessage)
        {
            var match = Regex.Match(
                requestMessage.Headers.Accept.ToString(),
                @"application/vnd.ed-fi.(?<ResourceName>\w+).(?<ProfileName>[\w\-]+).readable\+json");

            if (!match.Success)
            {
                return false;
            }
            
            return match.Groups["ProfileName"].Value == TestReadableProfileName.ToLower();
        }

        private bool UsesWritableContentType(HttpRequestMessage requestMessage)
        {
            var match = Regex.Match(
                requestMessage.Content.Headers.ContentType.ToString(),
                @"application/vnd.ed-fi.(?<ResourceName>\w+).(?<ProfileName>[\w\-]+).writable\+json");

            if (!match.Success)
            {
                return false;
            }
            
            return match.Groups["ProfileName"].Value == TestWritableProfileName.ToLower();
        }
    }
}
