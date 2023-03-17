// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Autofac.Features.Indexed;
using Bogus;
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
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using EdFi.Tools.ApiPublisher.Tests.Models;
using EdFi.Tools.ApiPublisher.Tests.Resources;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class KeyChangesTests
    {
        [TestFixture]
        public class When_publishing_natural_key_changes : TestFixtureAsyncBase
        {
            const int TestItemQuantity = 2;

            private ChangeProcessor _changeProcessor;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;
            private string[] _resourcesWithUpdatableKeys;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;
            private List<GenericResource<FakeKey>> _suppliedTargetResources;
            private List<KeyChange<FakeKey>> _suppliedKeyChanges;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;

            protected override async Task ArrangeAsync()
            {
                // -----------------------------------------------------------------
                //                      Source Requests
                // -----------------------------------------------------------------
                int changeVersion = 1001;

                // Initialize a generator for the fake natural key class
                var keyValueFaker = TestHelpers.GetKeyValueFaker();
                
                // Initialize a generator for the /keyChanges API response
                var keyChangeFaker = new Faker<KeyChange<FakeKey>>().StrictMode(true)
                    .RuleFor(o => o.Id, f => Guid.NewGuid().ToString("n"))
                    .RuleFor(o => o.ChangeVersion, f => changeVersion++)
                    .Ignore(o => o.OldKeyValues)
                    .RuleFor(o => o.OldKeyValuesObject, f => keyValueFaker.Generate())
                    .Ignore(o => o.NewKeyValues)
                    .RuleFor(o => o.NewKeyValuesObject, f => keyValueFaker.Generate());

                _suppliedKeyChanges = keyChangeFaker.Generate(TestItemQuantity);

                // Prepare the fake source API endpoint
                _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                    // Test-specific mocks
                    .AvailableChangeVersions(1100)
                    .ResourceCount(responseTotalCountHeader: TestItemQuantity)
                    .GetResourceData(@"/data/v3/ed-fi/\w+/keyChanges", _suppliedKeyChanges);

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                int i = 0;
                
                // Initialize a generator for a generic resource with a reference containing the key values
                var targetResourceFaker = new Faker<GenericResource<FakeKey>>().StrictMode(true)
                    .RuleFor(o => o.Id, f => Guid.NewGuid().ToString("n"))
                    .RuleFor(o => o.SomeReference, f => _suppliedKeyChanges[i++].OldKeyValuesObject)
                    .RuleFor(o => o.VehicleManufacturer, f => f.Vehicle.Manufacturer())
                    .RuleFor(o => o.VehicleYear, f => f.Date.Between(DateTime.Today.AddYears(-50), DateTime.Today).Year);

                _suppliedTargetResources = targetResourceFaker.Generate(TestItemQuantity);
                
                _fakeTargetRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                    .SetBaseUrl(MockRequests.TargetApiBaseUrl)
                    .SetDataManagementUrlSegment(EdFiApiConstants.DataManagementApiSegment)
                    .SetChangeQueriesUrlSegment(EdFiApiConstants.ChangeQueriesApiSegment)
                    .OAuthToken()
                    .ApiVersionMetadata()
                    .Dependencies();

                for (int j = 0; j < TestItemQuantity; j++)
                {
                    _fakeTargetRequestHandler.GetResourceData(
                        @"/data/v3/ed-fi/\w+",
                        _suppliedKeyChanges[j].OldKeyValuesObject.ToQueryStringParams(),
                        new[] { _suppliedTargetResources[j] });
                }
                // -----------------------------------------------------------------

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(1000);
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

                _resourcesWithUpdatableKeys = TestHelpers.Configuration.GetResourcesWithUpdatableKeys();
                
                var options = TestHelpers.GetOptions();
                var configurationStoreSection = null as IConfigurationSection; //new ConfigurationSection()

                _changeProcessorConfiguration = new ChangeProcessorConfiguration(
                    authorizationFailureHandling,
                    _resourcesWithUpdatableKeys,
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

                var sourceCurrentChangeVersionProvider = new EdFiApiSourceCurrentChangeVersionProvider(sourceEdFiApiClientProvider);
                var sourceIsolationApplicator = new EdFiApiSourceIsolationApplicator(sourceEdFiApiClientProvider);
                var dataSourceCapabilities = new EdFiApiSourceCapabilities(sourceEdFiApiClientProvider);
                var publishErrorsBlocksFactory = new PublishErrorsBlocksFactory(errorPublisher);
                var sourceCapabilities = A.Fake<ISourceCapabilities>();
                var sourceResourceItemProvider = A.Fake<ISourceResourceItemProvider>();
                var sourceConnectionDetails = A.Fake<ISourceConnectionDetails>();
                var finalizationActivities = A.Fake<IFinalizationActivity>();
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

            [Test]
            public void Should_probe_the_source_API_for_keyChanges_support_by_calling_the_first_resource_with_a_limit_of_1()
            {
                string keyChangeSupportProbingResourceName = _resourcesWithUpdatableKeys.OrderBy(x => x).FirstOrDefault();
                
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{keyChangeSupportProbingResourceName}{EdFiApiConstants.KeyChangesPathSuffix}",
                            A<HttpRequestMessage>.That.Matches(msg => !msg.HasParameter("totalCount") && msg.QueryString<int>("limit") == 1
                            )))
                    .MustHaveHappenedOnceExactly();                
            }

            [Test]
            public void Should_GET_keyChanges_from_source_API_for_each_resource_whose_keys_are_updatable()
            {
                // Console.WriteLine(_loggerRepository.LoggedContent());
                
                foreach (var resourceWithUpdatableKey in _resourcesWithUpdatableKeys)
                {
                    // One request for the count
                    A.CallTo(
                            () => _fakeSourceRequestHandler.Get(
                                $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{resourceWithUpdatableKey}{EdFiApiConstants.KeyChangesPathSuffix}",
                                A<HttpRequestMessage>.That.Matches(msg => msg.QueryString<bool>("totalCount") == true && msg.QueryString<int>("limit") <= 1
                                )))
                        .MustHaveHappenedOnceExactly();

                    // One request for the data (no count)
                    A.CallTo(
                            () => _fakeSourceRequestHandler.Get(
                                $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{resourceWithUpdatableKey}{EdFiApiConstants.KeyChangesPathSuffix}",
                                A<HttpRequestMessage>.That.Matches(msg => !msg.HasParameter("totalCount") && msg.QueryString<int>("limit") == TestHelpers.GetOptions().StreamingPageSize
                                )))
                        .MustHaveHappenedOnceExactly();
                }
            }

            [Test]
            public void Should_not_attempt_to_GET_keyChanges_from_source_API_for_any_resources_whose_keys_are_not_updatable()
            {
                var dependencies = XElement.Parse(TestData.Dependencies.GraphML());

                var ns = new XmlNamespaceManager(new NameTable());
                ns.AddNamespace("g", "http://graphml.graphdrawing.org/xmlns");
                
                var resourcePaths = dependencies
                    .XPathSelectElements("//g:node", ns)
                    .Select(x => x.Attribute("id")?.Value).ToArray();
                
                var resourcesWithoutUpdatableKeys = resourcePaths.Except(_resourcesWithUpdatableKeys).ToArray();

                foreach (var resourceWithoutUpdatableKeys in resourcesWithoutUpdatableKeys)
                {
                    A.CallTo(
                            () => _fakeSourceRequestHandler.Get(
                                $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{resourceWithoutUpdatableKeys}/keyChanges",
                                A<HttpRequestMessage>.Ignored))
                        .MustNotHaveHappened();
                }
            }

            [Test]
            public void Should_PUT_all_existing_target_API_resources_with_key_changes_with_the_new_key_values_applied_from_the_source_API()
            {
                // Console.WriteLine(_loggerRepository.LoggedContent());

                // Verify that all the updated resources were PUT to the target API
                for (int j = 0; j < TestItemQuantity; j++)
                {
                    foreach (var resourcesWithUpdatableKey in _resourcesWithUpdatableKeys)
                    {
                        A.CallTo(
                                () => _fakeTargetRequestHandler.Put(
                                    $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{resourcesWithUpdatableKey}/{_suppliedTargetResources[j].Id}",
                                    A<HttpRequestMessage>.That.Matches(msg => IsOriginalTargetResourceWithNewKeyValuesApplied(msg, _suppliedKeyChanges[j], _suppliedTargetResources[j]))))
                            .MustHaveHappenedOnceExactly();
                    }
                }
            }

            // [Test]
            // public void Should_log_something()
            // {
            //     // Inspect the log entries
            //     var memoryAppender = _loggerRepository.GetAppenders().OfType<MemoryAppender>().Single();
            //     var events = memoryAppender.GetEvents();
            // }

            private bool IsOriginalTargetResourceWithNewKeyValuesApplied(HttpRequestMessage request, KeyChange<FakeKey> suppliedKeyChange, GenericResource<FakeKey> suppliedTargetResource)
            {
                string content = request.Content.ReadAsStringAsync().Result;

                var requestItem = JsonConvert.DeserializeObject<GenericResource<FakeKey>>(content);
                
                requestItem.ShouldSatisfyAllConditions(
                    // The main values of the object should match the target
                    () => request.RequestUri.LocalPath.Split('/').Last().ShouldBe(suppliedTargetResource.Id), 
                    () => requestItem.VehicleManufacturer.ShouldBe(suppliedTargetResource.VehicleManufacturer), 
                    () => requestItem.VehicleYear.ShouldBe(suppliedTargetResource.VehicleYear),
                    // The key should match the source
                    () => requestItem.SomeReference.Name.ShouldBe(suppliedKeyChange.NewKeyValuesObject.Name),
                    () => requestItem.SomeReference.BirthDate.ShouldBe(suppliedKeyChange.NewKeyValuesObject.BirthDate), 
                    () => requestItem.SomeReference.RetirementAge.ShouldBe(suppliedKeyChange.NewKeyValuesObject.RetirementAge));

                return true;
            }
        }
   }
}
