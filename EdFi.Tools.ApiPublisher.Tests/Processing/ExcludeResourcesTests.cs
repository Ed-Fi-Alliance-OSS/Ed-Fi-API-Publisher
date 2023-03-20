// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
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
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Finalization;
using EdFi.Tools.ApiPublisher.Core.Isolation;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using FluentAssertions;
using Jering.Javascript.NodeJS;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Serilog;
using Serilog.Events;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class ExcludeResourcesTests
    {
        [TestFixture]
        public class When_excluding_publishing_of_a_resource_and_its_dependents : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;
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
                    exclude: new []{ "schools" });
            
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
                var sourceCapabilities = A.Fake<ISourceCapabilities>();
                var sourceResourceItemProvider = A.Fake<ISourceResourceItemProvider>();
                var sourceConnectionDetails = A.Fake<ISourceConnectionDetails>();
                var finalizationActivities = A.Fake<IFinalizationActivity>();
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
                            new PostResourceProcessingBlocksFactory(nodeJsService, targetEdFiApiClientProvider, sourceConnectionDetails, dataSourceCapabilities, sourceResourceItemProvider)));

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
                    new[] { finalizationActivities });
            }

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }

            [TestCase("/ed-fi/localEducationAgencies")]
            [TestCase("/ed-fi/educationOrganizationNetworks")]
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

            [TestCase("/ed-fi/schools")]
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

            [TestCase("/ed-fi/schools")]
            public void Should_reflect_the_processing_as_an_exclusion_with_its_dependents_in_the_log(string resourceCollectionUrl)
            {
                LogEvents
                   .Should()
                   .Contain(e => e.MessageTemplate.Text.Contains($"Excluding resource '{resourceCollectionUrl}' and its dependents..."))
                   .Which.Level
                   .Should()
                   .Be(LogEventLevel.Debug);
            }

            [TestCase("/ed-fi/sessions")]
            [TestCase("/ed-fi/studentSectionAssociations")]
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
            
            [TestCase("/ed-fi/assessments")] // Dependent on EducationOrganization
            [TestCase("/ed-fi/programs")] // Dependent on EducationOrganization
            [TestCase("/ed-fi/educationOrganizationNetworkAssociations")] // Dependent on EducationOrganization
            [TestCase("/tpdm/applications")] // Dependent on EducationOrganization
            public void Should_NOT_attempt_to_publish_resources_that_are_dependent_on_the_excluded_resources_base_class(string resourceCollectionUrl)
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
