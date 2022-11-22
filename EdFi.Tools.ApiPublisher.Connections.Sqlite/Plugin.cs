// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Modules;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Plugin;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite;

public class Plugin : IPlugin
{
    public const string SqliteConnectionType = "sqlite";

    public void ApplyConfiguration(string[] args, IConfigurationBuilder configBuilder)
    {
        configBuilder.AddCommandLine(args, new Dictionary<string, string>
        {
            ["--targetFile"] = "Connections:Target:File"
        });
    }

    public void PerformConfigurationRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot initialConfigurationRoot)
    {
        containerBuilder.RegisterModule(new PluginModule());
    }

    public void PerformFinalRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot finalConfigurationRoot)
    {
        // TODO: When support is added for Sqlite database as source  
        // string sourceConnectionType = ConfigurationHelper.GetSourceConnectionType(finalConfigurationRoot);
        //
        // if (sourceConnectionType == ApiConnectionType)
        // {
        //     containerBuilder.RegisterModule(new EdFiApiAsSourceModule(finalConfigurationRoot));
        // }

        string targetConnectionType = ConfigurationHelper.GetTargetConnectionType(finalConfigurationRoot);

        if (targetConnectionType == SqliteConnectionType)
        {
            containerBuilder.RegisterModule(new SqliteAsTargetModule(finalConfigurationRoot));
        }
    }
}
