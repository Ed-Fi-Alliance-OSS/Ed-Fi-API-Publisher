using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using log4net;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Versioning;

public class EdFiApiSourceCurrentChangeVersionProvider : ISourceCurrentChangeVersionProvider
{
    private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiApiSourceCurrentChangeVersionProvider));

    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;

    public EdFiApiSourceCurrentChangeVersionProvider(ISourceEdFiApiClientProvider sourceEdFiApiClientProvider)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
    }
    
    public async Task<long?> GetCurrentChangeVersionAsync()
    {
        var sourceApiClient = _sourceEdFiApiClientProvider.GetApiClient();
        
        // Get current source version information
        string availableChangeVersionsRelativePath = $"{sourceApiClient.ChangeQueriesApiSegment}/availableChangeVersions";

        var versionResponse = await sourceApiClient.HttpClient.GetAsync(availableChangeVersionsRelativePath)
            .ConfigureAwait(false);

        if (!versionResponse.IsSuccessStatusCode)
        {
            _logger.Warn(
                $"Unable to get current change version from source API at '{sourceApiClient.HttpClient.BaseAddress}{availableChangeVersionsRelativePath}' (response status: {versionResponse.StatusCode}). Full synchronization will always be performed against this source, and any concurrent changes made against the source may cause change processing to produce unreliable results.");

            return null;
        }

        string versionResponseText = await versionResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        _logger.Debug(
            $"Available change versions request from {sourceApiClient.HttpClient.BaseAddress}{availableChangeVersionsRelativePath} returned {versionResponse.StatusCode}: {versionResponseText}");

        try
        {
            long maxChangeVersion =

                // Versions of Ed-Fi API through at least v3.4
                (JObject.Parse(versionResponseText)["NewestChangeVersion"]

                    // Enhancements/fixes applied introduced as part of API Publisher work
                    ?? JObject.Parse(versionResponseText)["newestChangeVersion"]).Value<long>();

            return maxChangeVersion;
        }
        catch (Exception ex)
        {
            throw new Exception($"Unable to read 'newestChangeVersion' property from response.", ex);
        }
    }
}