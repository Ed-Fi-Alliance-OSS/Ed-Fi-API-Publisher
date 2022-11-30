using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public class TargetEdFiVersionMetadataCollector : ITargetEdFiApiVersionMetadataProvider
{
    private readonly ITargetEdFiApiVersionMetadataProvider _targetEdFiApiVersionMetadataProvider;
    private readonly IPublishingOperationMetadataCollector _metadataCollector;

    public TargetEdFiVersionMetadataCollector(
        ITargetEdFiApiVersionMetadataProvider targetEdFiApiVersionMetadataProvider,
        IPublishingOperationMetadataCollector metadataCollector)
    {
        _targetEdFiApiVersionMetadataProvider = targetEdFiApiVersionMetadataProvider;
        _metadataCollector = metadataCollector;
    }

    public async Task<JObject?> GetVersionMetadata()
    {
        var versionMetadata = await _targetEdFiApiVersionMetadataProvider.GetVersionMetadata();
        
        _metadataCollector.SetTargetVersionMetadata(versionMetadata);

        return versionMetadata;
    }
}