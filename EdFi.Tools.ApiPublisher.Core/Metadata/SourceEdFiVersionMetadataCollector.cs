using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public class SourceEdFiVersionMetadataCollector : ISourceEdFiApiVersionMetadataProvider
{
    private readonly ISourceEdFiApiVersionMetadataProvider _sourceEdFiApiVersionMetadataProvider;
    private readonly IPublishingOperationMetadataCollector _metadataCollector;

    public SourceEdFiVersionMetadataCollector(
        ISourceEdFiApiVersionMetadataProvider sourceEdFiApiVersionMetadataProvider,
        IPublishingOperationMetadataCollector metadataCollector)
    {
        _sourceEdFiApiVersionMetadataProvider = sourceEdFiApiVersionMetadataProvider;
        _metadataCollector = metadataCollector;
    }

    public async Task<JObject?> GetVersionMetadata()
    {
        var versionMetadata = await _sourceEdFiApiVersionMetadataProvider.GetVersionMetadata();
        
        _metadataCollector.SetSourceVersionMetadata(versionMetadata);

        return versionMetadata;
    }
}