// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using Autofac;
using Autofac.Core;
using Autofac.Core.Registration;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Cli;

public class EdFiApiClientsModule : Module
{
    private readonly IConfigurationRoot _finalConfiguration;

    public EdFiApiClientsModule(IConfigurationRoot finalConfiguration)
    {
        _finalConfiguration = finalConfiguration;
    }
    
    protected override void Load(ContainerBuilder builder)
    {
        var apiConnections = _finalConfiguration.Get<ConnectionConfiguration>().Connections;
        var options = _finalConfiguration.Get<ApiPublisherSettings>().Options;
        
        // Initialize source/target API clients
        var sourceApiConnectionDetails = apiConnections.Source;
        var targetApiConnectionDetails = apiConnections.Target;

        builder.RegisterInstance(sourceApiConnectionDetails).As<IEdFiDataSourceDetails>();
        builder.RegisterInstance(targetApiConnectionDetails).As<IEdFiDataSinkDetails>();
        
        var sourceEdFiApiClient = new Lazy<EdFiApiClient>(
            () => new EdFiApiClient(
                "Source",
                sourceApiConnectionDetails,
                options.BearerTokenRefreshMinutes,
                options.IgnoreSSLErrors));

        var targetEdFiApiClient = new Lazy<EdFiApiClient>(
            () => new EdFiApiClient(
                "Target",
                targetApiConnectionDetails,
                options.BearerTokenRefreshMinutes,
                options.IgnoreSSLErrors));

        builder.RegisterInstance(new EdFiApiClientProvider(sourceEdFiApiClient)).As<ISourceEdFiApiClientProvider>();
        builder.RegisterInstance(new EdFiApiClientProvider(targetEdFiApiClient)).As<ITargetEdFiApiClientProvider>();
    }
}
