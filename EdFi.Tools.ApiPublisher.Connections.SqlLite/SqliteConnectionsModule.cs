// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.SqlLite;

public class SqliteConnectionsModule : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        // Register the connection configuration details
        builder.RegisterType<SqliteConnectionDetails>().Named<INamedConnectionDetails>("sqlite");
    }
}
