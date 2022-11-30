using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;

public class EdFiApiVersionMetadataProviderBase
{
    private readonly string _role;
    private readonly IEdFiApiClientProvider _edFiApiClientProvider;

    private readonly ILog _logger;
    
    protected EdFiApiVersionMetadataProviderBase(string role, IEdFiApiClientProvider edFiApiClientProvider)
    {
        _role = role;
        _edFiApiClientProvider = edFiApiClientProvider;
        
        _logger = LogManager.GetLogger(GetType());
    }

    public async Task<JObject> GetVersionMetadata()
    {
        var versionResponse = _edFiApiClientProvider.GetApiClient().HttpClient.GetAsync("");

        if (!versionResponse.Result.IsSuccessStatusCode)
        {
            throw new Exception($"{_role} API at '{_edFiApiClientProvider.GetApiClient().HttpClient.BaseAddress}' returned status code '{versionResponse.Result.StatusCode}' for request for version information.");
        }
        
        string responseJson = await versionResponse.Result.Content.ReadAsStringAsync().ConfigureAwait(false);

        return GetVersionObject(responseJson);
        
        JObject GetVersionObject(string versionJson)
        {
            JObject versionObject;

            try
            {
                versionObject = JObject.Parse(versionJson);
                _logger.Info($"{_role} version information: {versionObject.ToString(Formatting.Indented)}");
            }
            catch (Exception)
            {
                throw new Exception($"Unable to parse version information returned from {_role.ToLower()} API.");
            }

            return versionObject;
        }
    }
}
