// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using Autofac.Core;
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
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
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

        builder.RegisterType<ApiSourceResourceItemProvider>()
            .As<ISourceResourceItemProvider>()
            .SingleInstance();

        // Register resource page message producer using a key set paging strategy
        if (options.UseKeySetPaging)
        {
            builder.RegisterType<EdFiApiKeysetPagingStreamResourcePageMessageProducer>()
                .As<IStreamResourcePageMessageProducer>()
                .SingleInstance();
        }
        // Register resource page message producer using a limit/offset paging strategy
        else
        {
            builder.RegisterType<EdFiApiLimitOffsetPagingStreamResourcePageMessageProducer>()
                .As<IStreamResourcePageMessageProducer>()
                .SingleInstance();
        }
        
        // Register handler to perform page-based requests against a Source API
        builder.RegisterType<EdFiApiStreamResourcePageMessageHandler>()
            .As<IStreamResourcePageMessageHandler>()
            .SingleInstance();

        // Register Data Source Total Count provider for Source API
        builder.RegisterType<EdFiApiSourceTotalCountProvider>()
            .As<ISourceTotalCountProvider>()
            .SingleInstance();
        
        // API dependency metadata from Ed-Fi ODS API (using Source API)
        if (options.UseSourceDependencyMetadata)
        {
            builder.RegisterType<EdFiApiGraphMLDependencyMetadataProvider>()
                .As<IGraphMLDependencyMetadataProvider>()
                .WithParameter(
                    // Configure to use with Target API
                    new ResolvedParameter(
                        (pi, ctx) => pi.ParameterType == typeof(IEdFiApiClientProvider),
                        (pi, ctx) => ctx.Resolve<ISourceEdFiApiClientProvider>()));
        }
    }
}
