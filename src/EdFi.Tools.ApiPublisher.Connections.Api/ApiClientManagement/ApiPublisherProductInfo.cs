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
            var version = fileVersion.FileVersion;
            var productInfo = new ProductInfoHeaderValue("Ed-Fi-API-Publisher", version);

            var targetFrameWorkAttributes = assembly.CustomAttributes.Where(attribute =>
                attribute.AttributeType.Name == nameof(TargetFrameworkAttribute)
            );
            var customAttribute = targetFrameWorkAttributes.FirstOrDefault();
            var customAttributeValue = customAttribute?.NamedArguments.FirstOrDefault();
            if (customAttributeValue != null)
            {
                var dotnetVersionValues = ((CustomAttributeNamedArgument)customAttributeValue).TypedValue.Value.ToString().Split(' ');
                if (dotnetVersionValues.Length > 0)
                {
                    var dotnetInfo = new ProductInfoHeaderValue(
                        dotnetVersionValues[0],
                        dotnetVersionValues[1]
                    );
                    httpClient.DefaultRequestHeaders.UserAgent.Add(dotnetInfo);
                }
            }
            httpClient.DefaultRequestHeaders.UserAgent.Add(productInfo);
        }
    }
}
