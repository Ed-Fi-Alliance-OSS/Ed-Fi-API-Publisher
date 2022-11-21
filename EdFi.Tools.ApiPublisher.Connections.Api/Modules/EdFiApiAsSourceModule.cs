// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;
using EdFi.Tools.ApiPublisher.Connections.Api.Source.Processing;
using EdFi.Tools.ApiPublisher.Connections.Api.Source.Processing.Capabilities;
using EdFi.Tools.ApiPublisher.Connections.Api.Source.Processing.Counting;
using EdFi.Tools.ApiPublisher.Connections.Api.Source.Processing.Isolation;
using EdFi.Tools.ApiPublisher.Connections.Api.Source.Processing.MessageHandlers;
using EdFi.Tools.ApiPublisher.Connections.Api.Source.Processing.MessageProducers;
using EdFi.Tools.ApiPublisher.Connections.Api.Source.Processing.Versioning;
using EdFi.Tools.ApiPublisher.Connections.Api.Target.Processing.Initiators;
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
        builder.RegisterType<EdFiApiSourceCurrentChangeVersionProvider>()
            .As<ISourceCurrentChangeVersionProvider>()
            .SingleInstance();

        // Version metadata for a Source API
        builder.RegisterType<SourceEdFiApiVersionMetadataProvider>()
            .As<ISourceEdFiApiVersionMetadataProvider>()
            .SingleInstance();

        // Snapshot Isolation applicator for Source API
        builder.RegisterType<EdFiApiSourceIsolationApplicator>()
            .As<ISourceIsolationApplicator>()
            .SingleInstance();

        // Determine data source capabilities for Source API
        builder.RegisterType<EdFiApiSourceCapabilities>()
            .As<ISourceCapabilities>()
            .SingleInstance();

        // Register resource page message producer using a limit/offset paging strategy
        builder.RegisterType<EdFiApiLimitOffsetPagingStreamResourcePageMessageProducer>()
            .As<IStreamResourcePageMessageProducer>()
            .SingleInstance();
        
        // Register handler to perform page-based requests against a Source API
        builder.RegisterType<EdFiApiStreamResourcePageMessageHandler>()
            .As<IStreamResourcePageMessageHandler>()
            .SingleInstance();

        // Register Data Source Total Count provider for Source API
        builder.RegisterType<EdFiApiSourceTotalCountProvider>()
            .As<ISourceTotalCountProvider>()
            .SingleInstance();
        
        // Register the processing stage initiators
        builder.RegisterType<ChangeKeysPublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.KeyChanges);
        builder.RegisterType<UpsertPublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.Upserts);
        builder.RegisterType<DeletePublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.Deletes);
    }
}