namespace EdFi.Tools.ApiPublisher.Core.Configuration;

public abstract class NamedConnectionDetailsBase : INamedConnectionDetails
{
    public string? Name { get; set; }

    public virtual bool IsFullyDefined() => true;

    public virtual bool NeedsResolution() => false;
}
