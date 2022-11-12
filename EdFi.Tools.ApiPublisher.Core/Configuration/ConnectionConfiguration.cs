using System;
using System.Linq;
using System.Collections.Generic;
using System.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ConnectionConfiguration
    {
        public Connections Connections { get; set; }
    }
    
    public class Connections
    {
        public ApiConnectionDetails Source { get; set; }
        public ApiConnectionDetails Target { get; set; }
    }

    public interface INamedSourceOrSink
    {
        string Name { get; set; }
        
        /// <summary>
        /// Gets or sets the explicitly provided value to use for the last change version processed.
        /// </summary>
        long? LastChangeVersionProcessed { get; set; }
        // TODO: Should this property really be here?
    }

    public interface IEdFiDataSourceDetails : INamedSourceOrSink
    {
        public bool? IgnoreIsolation { get; set; }

        IDictionary<string, long> LastChangeVersionProcessedByTargetName { get; }
        
        public string Include { get; set; }
        
        public string IncludeOnly { get; set; }
        
        public string Exclude { get; set; }
        
        public string ExcludeOnly { get; set; }
    }
    
    public interface IEdFiDataSinkDetails : INamedSourceOrSink { }
    
    public class ApiConnectionDetails : IEdFiDataSourceDetails, IEdFiDataSinkDetails
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Key { get; set; }
        public string Secret { get; set; }
        public string Scope { get; set; }
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
                    .ToDictionary(p => p.Name, p => p.Value.Value<long>(), StringComparer.OrdinalIgnoreCase);
            }
        }

        public IDictionary<string, long> LastChangeVersionProcessedByTargetName { get; private set; } =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public string Include { get; set; }
        public string IncludeOnly { get; set; }
        public string Exclude { get; set; }
        public string ExcludeOnly { get; set; }

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
}