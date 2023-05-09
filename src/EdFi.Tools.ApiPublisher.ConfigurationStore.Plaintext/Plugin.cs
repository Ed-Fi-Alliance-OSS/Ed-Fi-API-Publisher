// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.ConfigurationStore.Plaintext.Modules;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Plugin;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.Plaintext;

public class Plugin : IPlugin
{
    private const string ConfigurationProviderName = "plaintext";

    public void ApplyConfiguration(string[] args, IConfigurationBuilder configBuilder)
    {
        // Nothing to do
    }

    public void PerformConfigurationRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot initialConfigurationRoot)
    {
        if (initialConfigurationRoot.IsConnectionConfigurationProviderSelected(ConfigurationProviderName))
        {
            containerBuilder.RegisterModule(new PluginModule());
        }
    }

    public void PerformFinalRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot finalConfigurationRoot)
    {
        // Nothing to do
    }
}
