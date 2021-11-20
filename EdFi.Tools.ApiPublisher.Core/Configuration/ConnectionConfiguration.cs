using System;
using System.Linq;
using System.Collections.Generic;
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

    public class ApiConnectionDetails
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Key { get; set; }
        public string Secret { get; set; }
        public string Scope { get; set; }

        public bool? IgnoreIsolation { get; set; }

        public long? LastChangeVersionProcessed { get; set; }
        
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
                LastChangeVersionProcessedByTargetName = 
                    obj.Properties().ToDictionary(
                        p => p.Name,
                        p => p.Value.Value<long>(),
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        public IDictionary<string, long> LastChangeVersionProcessedByTargetName { get; private set; }
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public string Resources { get; set; }
        public string ExcludeResources { get; set; }
        public string SkipResources { get; set; }
        
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