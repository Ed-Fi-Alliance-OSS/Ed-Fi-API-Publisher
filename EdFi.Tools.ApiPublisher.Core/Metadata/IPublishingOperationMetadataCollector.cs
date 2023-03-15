using EdFi.Tools.ApiPublisher.Core.Processing;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public interface IPublishingOperationMetadataCollector
{
    void SetCurrentChangeVersion(long? changeVersion);
    void SetSourceVersionMetadata(JObject? versionMetadata);
    void SetTargetVersionMetadata(JObject? versionMetadata);
    void SetChangeWindow(ChangeWindow? changeWindow);
    void SetResourceItemCount(string resourcePath, long count);
    
    PublishingOperationMetadata GetMetadata();
}