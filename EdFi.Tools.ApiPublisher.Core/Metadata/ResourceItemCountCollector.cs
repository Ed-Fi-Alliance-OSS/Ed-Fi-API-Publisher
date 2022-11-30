using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public class ResourceItemCountCollector : ISourceTotalCountProvider
{
    private readonly ISourceTotalCountProvider _totalCountProvider;
    private readonly IPublishingOperationMetadataCollector _metadataCollector;

    public ResourceItemCountCollector(ISourceTotalCountProvider totalCountProvider, IPublishingOperationMetadataCollector metadataCollector)
    {
        _totalCountProvider = totalCountProvider;
        _metadataCollector = metadataCollector;
    }
    
    public async Task<(bool, long)> TryGetTotalCountAsync(
        string resourceUrl,
        Options options,
        ChangeWindow? changeWindow,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        CancellationToken cancellationToken)
    {
        var (success, count) = await _totalCountProvider.TryGetTotalCountAsync(
            resourceUrl,
            options,
            changeWindow,
            errorHandlingBlock,
            cancellationToken);

        _metadataCollector.SetResourceItemCount(resourceUrl, success ? count : -1);

        _metadataCollector.SetChangeWindow(changeWindow);
        
        return (success, count);
    }
}
