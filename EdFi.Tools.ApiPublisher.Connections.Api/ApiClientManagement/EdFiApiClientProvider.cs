using log4net;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;

public class EdFiApiClientProvider : ISourceEdFiApiClientProvider, ITargetEdFiApiClientProvider
{
    private readonly Lazy<EdFiApiClient> _apiClient;

    private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiApiClientProvider));
    
    public EdFiApiClientProvider(Lazy<EdFiApiClient> apiClient)
    {
        _apiClient = apiClient;
    }
    
    public EdFiApiClient GetApiClient()
    {
        if (!_apiClient.IsValueCreated)
        {
            // Establish connection to API
            _logger.Info($"Initializing API client '{_apiClient.Value.ConnectionDetails.Name}'...");
        }

        return _apiClient.Value;
    }
}
