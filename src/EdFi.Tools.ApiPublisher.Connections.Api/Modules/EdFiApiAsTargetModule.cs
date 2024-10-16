// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using Autofac.Core;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Dependencies;
using EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Initiators;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Modules;

public class EdFiApiAsTargetModule : Module
{
    private readonly IConfigurationRoot _finalConfiguration;

    public EdFiApiAsTargetModule(IConfigurationRoot finalConfiguration)
    {
        _finalConfiguration = finalConfiguration;
    }

    protected override void Load(ContainerBuilder builder)
    {
        var options = _finalConfiguration.Get<ApiPublisherSettings>().Options;

        // Initialize source/target API clients
        var connectionsConfiguration = _finalConfiguration.GetSection("Connections");
        var targetConnectionConfiguration = connectionsConfiguration.GetSection("Target");
        var targetApiConnectionDetails = targetConnectionConfiguration.Get<ApiConnectionDetails>();
        var rateLimiter = new PollyRateLimiter<HttpResponseMessage>(options);

        builder.RegisterInstance(targetApiConnectionDetails).As<ITargetConnectionDetails>();

        var targetEdFiApiClient = new Lazy<EdFiApiClient>(
            () => new EdFiApiClient(
                "Target",
                targetApiConnectionDetails,
                options.BearerTokenRefreshMinutes,
                options.IgnoreSSLErrors));

        builder.RegisterInstance(new EdFiApiClientProvider(targetEdFiApiClient))
            .As<ITargetEdFiApiClientProvider>()
            .SingleInstance();

        // Version metadata for a Target API
        builder.RegisterType<TargetEdFiApiVersionMetadataProvider>()
            .As<ITargetEdFiApiVersionMetadataProvider>()
            .SingleInstance();

        // API dependency metadata from Ed-Fi ODS API (using Target API)
        if (!options.UseSourceDependencyMetadata)
        {
            _ = builder.RegisterType<EdFiApiGraphMLDependencyMetadataProvider>()
                .As<IGraphMLDependencyMetadataProvider>()
                .WithParameter(
                    // Configure to use with Target API
                    new ResolvedParameter(
                        (pi, ctx) => pi.ParameterType == typeof(IEdFiApiClientProvider),
                        (pi, ctx) => ctx.Resolve<ITargetEdFiApiClientProvider>()));
        }

        // Target Data Processing
        builder.RegisterType<ChangeResourceKeyProcessingBlocksFactory>()
            .As<IProcessingBlocksFactory<GetItemForKeyChangeMessage>>()
            .WithParameter("rateLimiter", rateLimiter)
            .SingleInstance();

        builder.RegisterType<PostResourceProcessingBlocksFactory>()
            .As<IProcessingBlocksFactory<PostItemMessage>>()
            .WithParameter("rateLimiter", rateLimiter)
            .SingleInstance();

        builder.RegisterType<DeleteResourceProcessingBlocksFactory>()
            .As<IProcessingBlocksFactory<GetItemForDeletionMessage>>()
            .WithParameter("rateLimiter", rateLimiter)
            .SingleInstance();

        // Register the processing stage initiators
        builder.RegisterType<KeyChangePublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.KeyChanges);
        builder.RegisterType<UpsertPublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.Upserts);
        builder.RegisterType<DeletePublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.Deletes);
    }
}
