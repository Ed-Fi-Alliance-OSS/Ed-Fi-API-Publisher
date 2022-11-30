using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Versioning;

public class FallbackSourceEdFiApiVersionMetadataProvider : ISourceEdFiApiVersionMetadataProvider
{
    public Task<JObject?> GetVersionMetadata() => Task.FromResult(null as JObject);
}
