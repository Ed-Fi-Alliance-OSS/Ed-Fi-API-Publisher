// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Configuration;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Metadata.Versioning;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.Capabilities;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.Counting;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.MessageHandlers;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.MessageProducers;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.Versioning;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Modules;

public class SqliteAsSourceModule : Module
{
    private readonly IConfigurationRoot _finalConfiguration;

    public SqliteAsSourceModule(IConfigurationRoot finalConfiguration)
    {
        _finalConfiguration = finalConfiguration;
    }
    
    protected override void Load(ContainerBuilder builder)
    {
        var connectionsConfiguration = _finalConfiguration.GetSection("Connections");
        var connectionConfiguration = connectionsConfiguration.GetSection("Source");
        var sqliteConnectionDetails = connectionConfiguration.Get<SqliteConnectionDetails>();
        builder.RegisterInstance(sqliteConnectionDetails).As<ISourceConnectionDetails>();

        // Sqlite connection factory
        builder.Register(_ => new SqliteConnection($"DataSource={sqliteConnectionDetails.File}"));

        // Available ChangeVersions for Sqlite as source
        builder.RegisterType<SqliteSourceCurrentChangeVersionProvider>()
            .As<ISourceCurrentChangeVersionProvider>()
            .SingleInstance();
        
        // Version metadata for a Source API
        builder.RegisterType<SqliteSourceEdFiApiVersionMetadataProvider>()
            .As<ISourceEdFiApiVersionMetadataProvider>()
            .SingleInstance();
        
        // Determine data source capabilities for Sqlite as source
        builder.RegisterType<SqliteSourceCapabilities>().As<ISourceCapabilities>();
        
        // Register resource page message producer using a page-based strategy
        builder.RegisterType<SqliteStreamResourcePageMessageProducer>()
            .As<IStreamResourcePageMessageProducer>()
            .SingleInstance();

        // Register handler to perform page-based requests against Sqlite as source
        builder.RegisterType<SqliteStreamResourcePageMessageHandler>()
            .As<IStreamResourcePageMessageHandler>()
            .SingleInstance();

        // Register Data Source Total Count provider for Source API
        builder.RegisterType<SqliteSourceTotalCountProvider>().As<ISourceTotalCountProvider>();
    }
}
