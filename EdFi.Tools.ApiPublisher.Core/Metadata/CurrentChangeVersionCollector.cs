using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Versioning;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public class CurrentChangeVersionCollector : ISourceCurrentChangeVersionProvider
{
    private readonly ISourceCurrentChangeVersionProvider _currentChangeVersionProvider;
    private readonly IPublishingOperationMetadataCollector _metadataCollector;

    public CurrentChangeVersionCollector(
        ISourceCurrentChangeVersionProvider currentChangeVersionProvider,
        IPublishingOperationMetadataCollector metadataCollector)
    {
        _currentChangeVersionProvider = currentChangeVersionProvider;
        _metadataCollector = metadataCollector;
    }

    public async Task<long?> GetCurrentChangeVersionAsync()
    {
        var currentChangeVersion = await _currentChangeVersionProvider.GetCurrentChangeVersionAsync();
        
        _metadataCollector.SetCurrentChangeVersion(currentChangeVersion);

        return currentChangeVersion;
    }
}
