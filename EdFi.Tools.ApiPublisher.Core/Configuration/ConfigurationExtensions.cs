// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration;

public static class ConfigurationExtensions
{
    public static bool IsConnectionConfigurationProviderSelected(this IConfigurationRoot configurationRoot, string configurationProviderName)
    {
        return string.Equals(
            configurationProviderName,
            ConfigurationHelper.GetConfigurationStoreProviderName(configurationRoot),
            StringComparison.OrdinalIgnoreCase);
    }
}
