using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Versioning;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;

public class TargetEdFiApiVersionMetadataProvider : EdFiApiVersionMetadataProviderBase, ITargetEdFiApiVersionMetadataProvider
{
    public TargetEdFiApiVersionMetadataProvider(ITargetEdFiApiClientProvider targetEdFiApiClientProvider)
        : base("Target", targetEdFiApiClientProvider) { }
}
