// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Identifies the publisher and its runtime to the API. Shared by the client that publishes data and by the
    /// client that requests tokens, so that both are recognizable in an API's request log.
    /// </summary>
    internal static class ApiPublisherProductInfo
    {
        internal static void ApplyTo(HttpClient httpClient)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location);
            var productInfo = new ProductInfoHeaderValue("Ed-Fi-API-Publisher", fileVersion.FileVersion);

            // The display name reads like ".NET 10.0": a product and a version. Anything else is left off rather
            // than risk a malformed header.
            string[] frameworkNameAndVersion = assembly
                .GetCustomAttribute<TargetFrameworkAttribute>()
                ?.FrameworkDisplayName
                ?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (frameworkNameAndVersion is { Length: 2 })
            {
                httpClient.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue(frameworkNameAndVersion[0], frameworkNameAndVersion[1])
                );
            }

            httpClient.DefaultRequestHeaders.UserAgent.Add(productInfo);
        }
    }
}
