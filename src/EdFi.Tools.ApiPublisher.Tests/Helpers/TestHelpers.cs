// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac.Features.Indexed;
using Bogus;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
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
using EdFi.Tools.ApiPublisher.Tests.Models;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Net;

namespace EdFi.Tools.ApiPublisher.Tests.Helpers
{
	public class TestHelpers
    {
        public const string AnyResourcePattern = "/(ed-fi|tpdm)/\\w+";
        // public const string AnyResourcePattern = "/ed-fi/\\w+";

        public static Faker<GenericResource<FakeKey>> GetGenericResourceFaker()
        {
            var keyValueFaker = GetKeyValueFaker();

            // Initialize a generator for a generic resource with a reference containing the key values
            var genericResourceFaker = new Faker<GenericResource<FakeKey>>().StrictMode(true)
                .RuleFor(o => o.Id, f => Guid.NewGuid().ToString("n"))
                .RuleFor(o => o.SomeReference, f => keyValueFaker.Generate())
                .RuleFor(o => o.VehicleManufacturer, f => f.Vehicle.Manufacturer())
                .RuleFor(o => o.VehicleYear, f => f.Date.Between(DateTime.Today.AddYears(-50), DateTime.Today).Year);

            return genericResourceFaker;
        }

        public static Faker<FakeKey> GetKeyValueFaker()
        {
            var linkValueFaker = GetLinkValueFaker();
            
            // Initialize a generator for the fake natural key class
            var keyValueFaker = new Faker<FakeKey>().StrictMode(true)
                .RuleFor(o => o.Name, f => f.Name.FirstName())
                .RuleFor(o => o.RetirementAge, f => f.Random.Int(50, 75))
                .RuleFor(o => o.BirthDate, f => f.Date.Between(DateTime.Today.AddYears(-75), DateTime.Today.AddYears(5)).Date)
                .RuleFor(o => o.Link, f => linkValueFaker.Generate());

            return keyValueFaker;
        }

        public static Faker<Link> GetLinkValueFaker(string href = null, string rel = "Some")
        {
            var linkFaker = new Faker<Link>().StrictMode(true)
                .RuleFor(o => o.Rel, () => rel)
                .RuleFor(o => o.Href, f => href ?? $"/ed-fi/somethings/{Guid.NewGuid():n}");

            return linkFaker;
        }

        public static Options GetOptions()
        {
            return new Options
            {
                IncludeDescriptors = true,
                MaxRetryAttempts = 2,
                StreamingPageSize = 50,
                BearerTokenRefreshMinutes = 27,
                ErrorPublishingBatchSize = 50,
                RetryStartingDelayMilliseconds = 1,
                IgnoreSSLErrors = true,
                StreamingPagesWaitDurationSeconds = 10,
                MaxDegreeOfParallelismForResourceProcessing = 2,
                MaxDegreeOfParallelismForPostResourceItem = 1,
                MaxDegreeOfParallelismForStreamResourcePages = 1,
                WhatIf = false,
            };
        }

        public static ApiConnectionDetails GetSourceApiConnectionDetails(
            int lastVersionProcessedToTarget = 1000,
            string[] include = null,
            string[] includeOnly = null,
            string[] exclude = null,
            string[] excludeOnly = null,
            bool ignoreIsolation = false,
            int? schoolYear = null,
            string profileName = null)
        {
            return new ApiConnectionDetails
            {
                Name = "TestSource",
                Url = MockRequests.SourceApiBaseUrl,
                Key = "sourceKey",
                Secret = "secret",
                Scope = null,
                SchoolYear = schoolYear,
               
                Include = include == null ? null : string.Join(",", include),
                IncludeOnly = includeOnly == null ? null : string.Join(",", includeOnly),
                Exclude = exclude == null ? null : string.Join(",", exclude),
                ExcludeOnly = excludeOnly == null ? null : string.Join(",", excludeOnly),

                IgnoreIsolation = ignoreIsolation,
                
                // LastChangeVersionProcessed = null,
                // LastChangeVersionsProcessed = "{ 'TestTarget': 1234 }",
                TreatForbiddenPostAsWarning = true,
                LastChangeVersionProcessedByTargetName =
                {
                    { "TestTarget", lastVersionProcessedToTarget },
                },
                ProfileName = profileName,
            };
        }

        public static ApiConnectionDetails GetTargetApiConnectionDetails(int? schoolYear = null, string profileName = null)
        {
            return new ApiConnectionDetails
            {
                Name = "TestTarget",
                Url = MockRequests.TargetApiBaseUrl,
                Key = "targetKey",
                Secret = "secret",
                Scope = null,
                SchoolYear = schoolYear,

                Include = null, // "abc,def,ghi",
                Exclude = null,
                ExcludeOnly = null,
                
                IgnoreIsolation = true,
                
                LastChangeVersionProcessed = null,
                LastChangeVersionsProcessed = null,
                TreatForbiddenPostAsWarning = true,
                LastChangeVersionProcessedByTargetName = {},

                ProfileName = profileName,
            };
        }

        public static class Configuration
        {
            public static string[] GetResourcesWithUpdatableKeys()
            {
                return new[]
                {
                    "/ed-fi/classPeriods",
                    "/ed-fi/grades",
                    "/ed-fi/gradebookEntries",
                    "/ed-fi/locations",
                    "/ed-fi/sections",
                    "/ed-fi/sessions",
                    "/ed-fi/studentSchoolAssociations",
                    "/ed-fi/studentSectionAssociations",
                };
            }

            public static AuthorizationFailureHandling[] GetAuthorizationFailureHandling()
            {
                return new []
                {
                    new AuthorizationFailureHandling
                    {
                        Path = "/ed-fi/students",
                        UpdatePrerequisitePaths = new[] { "/ed-fi/studentSchoolAssociations" }
                    },
                    new AuthorizationFailureHandling
                    {
                        Path = "/ed-fi/staffs",
                        UpdatePrerequisitePaths = new[]
                        {
                            "/ed-fi/staffEducationOrganizationEmploymentAssociations",
                            "/ed-fi/staffEducationOrganizationAssignmentAssociations"
                        }
                    },
                    new AuthorizationFailureHandling
                    {
                        Path = "/ed-fi/parents",
                        UpdatePrerequisitePaths = new[] { "/ed-fi/studentParentAssociations" }
                    }
                };
            }
        }

        public static void InitializeLogging()
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.TestCorrelator()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .CreateLogger();
        }

        public static IFakeHttpRequestHandler GetFakeBaselineSourceApiRequestHandler(
                string dataManagementUrlSegment = EdFiApiConstants.DataManagementApiSegment,
                string changeQueriesUrlSegment = EdFiApiConstants.ChangeQueriesApiSegment)
        {
            return A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .SetDataManagementUrlSegment(dataManagementUrlSegment)
                .SetChangeQueriesUrlSegment(changeQueriesUrlSegment)
                .OAuthToken()
                .ApiVersionMetadata()
                .Snapshots(new []{ new Snapshot { Id = Guid.NewGuid(), SnapshotIdentifier = "ABC123", SnapshotDateTime = DateTime.Now } })
                .LegacySnapshotsNotFound();
        }

        public static IFakeHttpRequestHandler GetFakeBaselineTargetApiRequestHandler(
            string dataManagementUrlSegment = EdFiApiConstants.DataManagementApiSegment,
            string changeQueriesUrlSegment = EdFiApiConstants.ChangeQueriesApiSegment)
        {
            return A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.TargetApiBaseUrl)
                .SetDataManagementUrlSegment(dataManagementUrlSegment)
                .SetChangeQueriesUrlSegment(changeQueriesUrlSegment)
                .OAuthToken()
                .ApiVersionMetadata()
                .Dependencies();
        }



        public static ChangeProcessorConfiguration CreateChangeProcessorConfiguration(
            Options options,
            Func<string> javascriptModuleFactory = null,
            string[] resourcesWithUpdatableKeys = null)
        {
            return new(
                options,
                Configuration.GetAuthorizationFailureHandling(),
                resourcesWithUpdatableKeys ?? Array.Empty<string>(),
                A.Fake<IConfigurationSection>(),
                javascriptModuleFactory
            );
        }

        public static ChangeProcessor CreateChangeProcessorWithDefaultDependencies(
            Options options,
            ApiConnectionDetails sourceApiConnectionDetails,
            IFakeHttpRequestHandler fakeSourceRequestHandler,
            ApiConnectionDetails targetApiConnectionDetails,
            IFakeHttpRequestHandler fakeTargetRequestHandler,
            INodeJSService nodeJsService = null,
            bool withReversePaging = false)
        {
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

            var sourceEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(SourceApiClientFactory));
            var targetEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory));

            var resourceDependencyMetadataProvider = new EdFiApiGraphMLDependencyMetadataProvider(targetEdFiApiClientProvider);
            var resourceDependencyProvider = new ResourceDependencyProvider(resourceDependencyMetadataProvider);
            var changeVersionProcessedWriter = A.Fake<IChangeVersionProcessedWriter>();
            var errorPublisher = A.Fake<IErrorPublisher>();

            nodeJsService ??= A.Fake<INodeJSService>();

            var sourceEdFiVersionMetadataProvider = new SourceEdFiApiVersionMetadataProvider(sourceEdFiApiClientProvider);
            var targetEdFiVersionMetadataProvider = new TargetEdFiApiVersionMetadataProvider(targetEdFiApiClientProvider);

            var edFiVersionsChecker = new EdFiVersionsChecker(sourceEdFiVersionMetadataProvider, targetEdFiVersionMetadataProvider);

            var sourceCurrentChangeVersionProvider = new EdFiApiSourceCurrentChangeVersionProvider(sourceEdFiApiClientProvider);
            var sourceIsolationApplicator = new EdFiApiSourceIsolationApplicator(sourceEdFiApiClientProvider);
            var dataSourceCapabilities = new EdFiApiSourceCapabilities(sourceEdFiApiClientProvider);
            var publishErrorsBlocksFactory = new PublishErrorsBlocksFactory(errorPublisher);

            var streamingResourceProcessor = new StreamingResourceProcessor(
                new StreamResourceBlockFactory(
                    (withReversePaging) ? 
                        new EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer(
                            new EdFiApiSourceTotalCountProvider(sourceEdFiApiClientProvider)) :
                        new EdFiApiLimitOffsetPagingStreamResourcePageMessageProducer(
                            new EdFiApiSourceTotalCountProvider(sourceEdFiApiClientProvider))
                    ),
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
                        new PostResourceProcessingBlocksFactory(
                            nodeJsService,
                            targetEdFiApiClientProvider,
                            sourceApiConnectionDetails,
                            dataSourceCapabilities,
                            new ApiSourceResourceItemProvider(sourceEdFiApiClientProvider, options))));

            A.CallTo(() => stageInitiators[PublishingStage.Deletes])
                .Returns(
                    new DeletePublishingStageInitiator(
                        streamingResourceProcessor,
                        new DeleteResourceProcessingBlocksFactory(targetEdFiApiClientProvider)));

            return new ChangeProcessor(
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
    }

    public static class IFakeTargetRequestHandlerExtensions
    {
        public static void EveryDataManagementPostReturns200Ok(this IFakeHttpRequestHandler fakeHttpRequestHandler)
        {
            fakeHttpRequestHandler.PostResource($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}", HttpStatusCode.OK);
        }
    }
}
