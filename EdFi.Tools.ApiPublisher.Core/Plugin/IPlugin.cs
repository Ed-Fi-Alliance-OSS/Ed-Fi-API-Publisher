// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Plugin;

public interface IPlugin
{
    void ApplyConfiguration(string[] args, IConfigurationBuilder configBuilder);
    
    void PerformConfigurationRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot initialConfigurationRoot);
    
    void PerformFinalRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot finalConfigurationRoot);
}
