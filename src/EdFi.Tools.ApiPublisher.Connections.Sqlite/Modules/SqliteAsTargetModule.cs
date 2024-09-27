// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Configuration;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Finalization;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Initiators;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Finalization;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Modules;

public class SqliteAsTargetModule : Module
{
    private readonly IConfigurationRoot _finalConfiguration;

    public SqliteAsTargetModule(IConfigurationRoot finalConfiguration)
    {
        _finalConfiguration = finalConfiguration;
    }

    protected override void Load(ContainerBuilder builder)
    {
        var connectionsConfiguration = _finalConfiguration.GetSection("Connections");
        var targetConnectionConfiguration = connectionsConfiguration.GetSection("Target");
        var targetSqliteConnectionDetails = targetConnectionConfiguration.Get<SqliteConnectionDetails>();
        builder.RegisterInstance(targetSqliteConnectionDetails).As<ITargetConnectionDetails>();

        builder.Register(_ => new SqliteConnection($"DataSource={targetSqliteConnectionDetails.File}"));

        // Target Data Processing
        builder.RegisterType<ChangeResourceKeyProcessingBlocksFactory>()
            .As<IProcessingBlocksFactory<KeyChangesJsonMessage>>()
            .SingleInstance();

        builder.RegisterType<UpsertProcessingBlocksFactory>()
            .As<IProcessingBlocksFactory<UpsertsJsonMessage>>()
            .SingleInstance();

        builder.RegisterType<DeleteResourceProcessingBlocksFactory>()
            .As<IProcessingBlocksFactory<DeletesJsonMessage>>()
            .SingleInstance();

        // Register the processing stage initiators
        builder.RegisterType<KeyChangePublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.KeyChanges);
        builder.RegisterType<UpsertPublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.Upserts);
        builder.RegisterType<DeletePublishingStageInitiator>().Keyed<IPublishingStageInitiator>(PublishingStage.Deletes);

        // Register a finalization step to record publishing operation metadata
        builder.RegisterType<SavePublishingOperationMetadataFinalizationActivity>().As<IFinalizationActivity>();
    }
}
