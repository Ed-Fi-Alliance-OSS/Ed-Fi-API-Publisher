// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Configuration;

public class ApiConnectionDetails : NamedConnectionDetails, ISourceConnectionDetails, ITargetConnectionDetails
{
    public string? Url { get; set; }
    public string? Key { get; set; }
    public string? Secret { get; set; }
    public string? Scope { get; set; }
    public int? SchoolYear { get; set; }
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

    public string? Include { get; set; }
    public string? IncludeOnly { get; set; }
    public string? Exclude { get; set; }
    public string? ExcludeOnly { get; set; }

    [Obsolete(
        "The 'Resources' configuration setting has been replaced by 'Include'. Adjust your connection configuration appropriately and try again.")]
    public string Resources
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                throw new ConfigurationErrorsException(
                    "The 'Connections:Source:Resources' configuration setting has been replaced by 'Connections:Source:Include'. Adjust your connection configuration appropriately and try again.");
            }
        }
    }

    [Obsolete(
        "The 'ExcludeResources' configuration setting has been replaced by 'Exclude'. Adjust your connection configuration appropriately and try again.")]
    public string ExcludeResources
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                throw new ConfigurationErrorsException(
                    "The 'Connections:Source:ExcludeResources' configuration setting has been replaced by 'Connections:Source:Exclude'. Adjust your connection configuration appropriately and try again.");
            }
        }
    }

    [Obsolete("The 'SkipResources' configuration setting has been replaced by 'ExcludeOnly'. Adjust your connection configuration appropriately and try again.")]
    public string SkipResources
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                throw new ConfigurationErrorsException("The 'Connections:Source:SkipResources' configuration setting has been replaced by 'Connections:Source:ExcludeOnly'. Adjust your connection configuration appropriately and try again.");
            }
        }
    }

    public bool? TreatForbiddenPostAsWarning { get; set; }

    public bool IsFullyDefined()
    {
        return (Url != null && Key != null && Secret != null);
    }

    public IEnumerable<string> MissingConfigurationValues()
    {
        if (Url == null)
        {
            yield return "Url";
        }
            
        if (Key == null)
        {
            yield return "Key";
        }
            
        if (Secret == null)
        {
            yield return "Secret";
        }
    }
        
    public bool NeedsResolution()
    {
        return !IsFullyDefined() && !string.IsNullOrEmpty(Name);
    }
}
