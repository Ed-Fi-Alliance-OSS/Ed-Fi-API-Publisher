using System.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Configuration;

public class ApiConnectionDetails : SourceConnectionDetailsBase, ISourceConnectionDetails, ITargetConnectionDetails
{
    public string? Url { get; set; }
    public string? Key { get; set; }
    public string? Secret { get; set; }
    public string? Scope { get; set; }
    public int? SchoolYear { get; set; }

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
