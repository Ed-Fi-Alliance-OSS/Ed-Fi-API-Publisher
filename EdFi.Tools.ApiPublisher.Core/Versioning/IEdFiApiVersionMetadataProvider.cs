using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Versioning;

public interface IEdFiApiVersionMetadataProvider
{
    Task<JObject?> GetVersionMetadata();
}