// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using Autofac.Extensions.DependencyInjection;
using EdFi.Tools.ApiPublisher.Core.NodeJs;
using Jering.Javascript.NodeJS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Tools.ApiPublisher.Core.Modules;

public class NodeJsRemediationsModule : Module
{
    private readonly IConfigurationRoot _initialConfiguration;

    public NodeJsRemediationsModule(IConfigurationRoot initialConfiguration)
    {
        this._initialConfiguration = initialConfiguration;
    }
    
    protected override void Load(ContainerBuilder builder)
    {
        string remediationsScriptFile = _initialConfiguration.GetValue<string>("Options:RemediationsScriptFile");

        var services = new ServiceCollection();

        if (!string.IsNullOrEmpty(remediationsScriptFile))
        {
            // Add support for NodeJS
            services.AddNodeJS();

            // Allow for multiple node processes to support processing
            services.Configure<OutOfProcessNodeJSServiceOptions>(options => { options.Concurrency = Concurrency.MultiProcess; });
        }
        else
        {
            // Provide an instance of an implementations that throws exceptions if called
            services.AddSingleton<INodeJSService>(new NullNodeJsService());
        }

        // Populate the container with the service collection
        builder.Populate(services);
    }
}
