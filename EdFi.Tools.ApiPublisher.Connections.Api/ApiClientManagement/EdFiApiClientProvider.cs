using Serilog;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;

public class EdFiApiClientProvider : ISourceEdFiApiClientProvider, ITargetEdFiApiClientProvider
{
    private readonly Lazy<EdFiApiClient> _apiClient;

    private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiClientProvider));
    
    public EdFiApiClientProvider(Lazy<EdFiApiClient> apiClient)
    {
        _apiClient = apiClient;
    }
    
    public EdFiApiClient GetApiClient()
    {
        if (!_apiClient.IsValueCreated)
        {
            // Establish connection to API
            _logger.Information($"Initializing API client '{_apiClient.Value.ConnectionDetails.Name}'...");
        }

        return _apiClient.Value;
    }
}
