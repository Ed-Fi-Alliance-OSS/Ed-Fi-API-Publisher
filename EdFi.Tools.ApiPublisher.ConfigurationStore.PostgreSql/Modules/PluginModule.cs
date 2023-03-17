// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql.Modules
{
    public class PluginModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<PostgreSqlConfigurationChangeVersionProcessedWriter>()
                .As<IChangeVersionProcessedWriter>()
                .SingleInstance();
            
            builder.RegisterType<PostgreSqlConfigurationNamedApiConnectionDetailsReader>()
                .As<INamedApiConnectionDetailsReader>()
                .SingleInstance();
        }
    }
}
