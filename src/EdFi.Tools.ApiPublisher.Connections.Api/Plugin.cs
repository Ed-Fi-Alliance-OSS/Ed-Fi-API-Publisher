// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Api.Modules;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Plugin;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Api;

public class Plugin : IPlugin
{
    public const string ApiConnectionType = "api";

    public void ApplyConfiguration(string[] args, IConfigurationBuilder configBuilder)
    {
        // Nothing to do
    }

    public void PerformConfigurationRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot initialConfigurationRoot)
    {
        containerBuilder.RegisterModule(new PluginModule());
    }

    public void PerformFinalRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot finalConfigurationRoot)
    {
        string sourceConnectionType = ConfigurationHelper.GetSourceConnectionType(finalConfigurationRoot);

        if (sourceConnectionType == ApiConnectionType)
        {
            containerBuilder.RegisterModule(new EdFiApiAsSourceModule(finalConfigurationRoot));
        }

        string targetConnectionType = ConfigurationHelper.GetTargetConnectionType(finalConfigurationRoot);

        if (targetConnectionType == ApiConnectionType)
        {
            containerBuilder.RegisterModule(new EdFiApiAsTargetModule(finalConfigurationRoot));
        }
    }
}
