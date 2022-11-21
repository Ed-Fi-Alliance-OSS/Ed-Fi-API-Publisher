// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Handling;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Initiators;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Isolation;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Modules;

public class EdFiApiAsSourceModule : Module
{
    private readonly IConfigurationRoot _finalConfiguration;

    public EdFiApiAsSourceModule(IConfigurationRoot finalConfiguration)
    {
        _finalConfiguration = finalConfiguration;
    }
    
    protected override void Load(ContainerBuilder builder)
    {
        var options = _finalConfiguration.Get<ApiPublisherSettings>().Options;
        
        // Initialize source/target API clients
        var connectionsConfiguration = _finalConfiguration.GetSection("Connections");
        var sourceConnectionConfiguration = connectionsConfiguration.GetSection("Source");
        var sourceApiConnectionDetails = sourceConnectionConfiguration.Get<ApiConnectionDetails>();

        builder.RegisterInstance(sourceApiConnectionDetails).As<ISourceConnectionDetails>();
        
        var sourceEdFiApiClient = new Lazy<EdFiApiClient>(
            () => new EdFiApiClient(
                "Source",
                sourceApiConnectionDetails,
                options.BearerTokenRefreshMinutes,
                options.IgnoreSSLErrors));

        builder.RegisterInstance(new EdFiApiClientProvider(sourceEdFiApiClient))
            .As<ISourceEdFiApiClientProvider>()
            .SingleInstance();
        
        // Available ChangeVersions for Source API
        builder.RegisterType<EdFiOdsApiSourceCurrentChangeVersionProvider>()
            .As<ISourceCurrentChangeVersionProvider>()
            .SingleInstance();

        // Version metadata for a Source API
        builder.RegisterType<SourceEdFiOdsApiVersionMetadataProvider>()
            .As<ISourceEdFiOdsApiVersionMetadataProvider>()
            .SingleInstance();

        // Snapshot Isolation applicator for Source API
        builder.RegisterType<EdFiOdsApiSourceIsolationApplicator>()
            .As<ISourceIsolationApplicator>()
            .SingleInstance();

        // Determine data source capabilities for Source API
        builder.RegisterType<EdFiApiDataSourceCapabilities>()
            .As<IDataSourceCapabilities>()
            .SingleInstance();

        // Register resource page message producer using a limit/offset paging strategy
        builder.RegisterType<EdFiOdsApiLimitOffsetPagingStreamResourcePageMessageProducer>()
            .As<IStreamResourcePageMessageProducer>()
            .SingleInstance();
        
        // Register handler to perform page-based requests against a Source API
        builder.RegisterType<ApiStreamResourcePageMessageHandler>()
            .As<IStreamResourcePageMessageHandler>()
            .SingleInstance();

        // Register Data Source Total Count provider for Source API
        builder.RegisterType<EdFiOdsApiDataSourceTotalCountProvider>()
            .As<IEdFiDataSourceTotalCountProvider>()
            .SingleInstance();
        
        // Register the processing stage initiators
        builder.RegisterType<ChangeKeysPublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.KeyChanges);
        builder.RegisterType<UpsertPublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.Upserts);
        builder.RegisterType<DeletePublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.Deletes);
    }
}