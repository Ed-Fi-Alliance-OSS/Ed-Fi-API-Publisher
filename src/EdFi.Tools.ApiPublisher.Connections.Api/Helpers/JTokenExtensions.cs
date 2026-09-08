// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Helpers;

public static class JTokenExtensions
{
    /// <summary>
    /// Safely extracts a token's string value, returning null instead of throwing when the token is
    /// absent, JSON null, or a non-scalar value (a JObject or JArray, whose Value&lt;string&gt;() throws).
    /// </summary>
    public static string SafeValue(this JToken token) => (token as JValue)?.Value<string>();
}
