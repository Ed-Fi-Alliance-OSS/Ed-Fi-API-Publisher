// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Core.Plugin;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.SqlLite;

public class Plugin : IPlugin
{
    public void ApplyConfiguration(string[] args, IConfigurationBuilder configBuilder)
    {
        configBuilder.AddCommandLine(args, new Dictionary<string, string>
        {
            ["--targetFile"] = "Connections:Target:File"
        });
    }

    public void PerformConfigurationRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot initialConfigurationRoot)
    {
        // Nothing to do
    }

    public void PerformFinalRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot finalConfigurationRoot)
    {
        // Nothing to do
    }
}
