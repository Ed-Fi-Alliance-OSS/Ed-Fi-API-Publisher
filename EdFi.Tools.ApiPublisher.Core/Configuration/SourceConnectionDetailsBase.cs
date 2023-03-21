// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Configuration;

public class SourceConnectionDetailsBase : NamedConnectionDetailsBase, ISourceConnectionDetails
{
    public bool? IgnoreIsolation { get; set; }

    /// <summary>
    /// Gets or sets the explicitly provided value to use for the last change version processed.
    /// </summary>
    public long? LastChangeVersionProcessed { get; set; }

    /// <summary>
    /// Gets or sets a JSON object representing the change versions processed by target connection name.
    /// </summary>
    public string LastChangeVersionsProcessed
    {
        get
        {
            var obj = new JObject();

            // Convert the dictionary to a JObject
            foreach (var kvp in LastChangeVersionProcessedByTargetName)
            {
                obj[kvp.Key] = kvp.Value;
            }

            // Serialize the JObject to a JSON string
            return obj.ToString(Formatting.None);
        }
        set
        {
            // Parse the JSON string value
            var obj = JObject.Parse(string.IsNullOrEmpty(value) ? "{}" : value);

            // Convert the parsed JSON to a case-insensitive dictionary
            LastChangeVersionProcessedByTargetName = obj.Properties()
                .ToDictionary(p => p.Name, p => Newtonsoft.Json.Linq.Extensions.Value<long>(p.Value), StringComparer.OrdinalIgnoreCase);
        }
    }

    public IDictionary<string, long> LastChangeVersionProcessedByTargetName { get; private set; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public string Include { get; set; }
    public string IncludeOnly { get; set; }
    public string Exclude { get; set; }
    public string ExcludeOnly { get; set; }
}
