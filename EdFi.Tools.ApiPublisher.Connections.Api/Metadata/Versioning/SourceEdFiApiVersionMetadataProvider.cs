using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Versioning;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;

public class SourceEdFiApiVersionMetadataProvider : EdFiApiVersionMetadataProviderBase, ISourceEdFiApiVersionMetadataProvider
{
    public SourceEdFiApiVersionMetadataProvider(ISourceEdFiApiClientProvider sourceEdFiApiClientProvider)
        : base("Source", sourceEdFiApiClientProvider) { }
}
