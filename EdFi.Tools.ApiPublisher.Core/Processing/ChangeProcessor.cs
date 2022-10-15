using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Version = EdFi.Tools.ApiPublisher.Core.Helpers.Version;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    public static class Conventions
    {
        public const string RetryKeySuffix = "#Retry";
    }
    
    public class ChangeProcessor : IChangeProcessor
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(ChangeProcessor));

        private readonly IResourceDependencyProvider _resourceDependencyProvider;
        private readonly IChangeVersionProcessedWriter _changeVersionProcessedWriter;
        private readonly IErrorPublisher _errorPublisher;
        private readonly IPostResourceBlocksFactory _postResourceBlocksFactory;

        public ChangeProcessor(
            IResourceDependencyProvider resourceDependencyProvider,
            IChangeVersionProcessedWriter changeVersionProcessedWriter,
            IErrorPublisher errorPublisher,
            IPostResourceBlocksFactory postResourceBlocksFactory)
        {
            _resourceDependencyProvider = resourceDependencyProvider;
            _changeVersionProcessedWriter = changeVersionProcessedWriter;
            _errorPublisher = errorPublisher;
            _postResourceBlocksFactory = postResourceBlocksFactory;
        }
        
        public async Task ProcessChangesAsync(ChangeProcessorConfiguration configuration, CancellationToken cancellationToken)
        {
            var processStopwatch = new Stopwatch();
            processStopwatch.Start();
            
            var authorizationFailureHandling = configuration.AuthorizationFailureHandling;
            var sourceApiConnectionDetails = configuration.SourceApiConnectionDetails;
            var targetApiConnectionDetails = configuration.TargetApiConnectionDetails;
            var sourceApiClient = configuration.SourceApiClient;
            var targetApiClient = configuration.TargetApiClient;
            var options = configuration.Options;
            var javascriptModuleFactory = configuration.JavascriptModuleFactory;

            _logger.Debug($"Options for processing:{Environment.NewLine}{JsonConvert.SerializeObject(options, Formatting.Indented)}");
            
            try
            {
                // Check Ed-Fi API and Standard versions for compatibility
                await CheckApiVersionsAsync(configuration).ConfigureAwait(false);

                // Look for and apply snapshots if flag was not provided
                if (sourceApiConnectionDetails.IgnoreIsolation != true)
                {
                    // Determine if source API provides a snapshot, and apply the HTTP header to the client 
                    await ApplySourceSnapshotIdentifierAsync(
                            sourceApiClient,
                            sourceApiConnectionDetails,
                            configuration.SourceApiVersion)
                        .ConfigureAwait(false);
                }

                // Establish the change window we're processing, if any.
                ChangeWindow changeWindow = null;

                // Only named (managed) connections can use a Change Window for processing.
                if (!string.IsNullOrWhiteSpace(sourceApiConnectionDetails.Name) 
                    && !string.IsNullOrWhiteSpace(targetApiConnectionDetails.Name))
                {
                    changeWindow = await EstablishChangeWindow(
                            sourceApiClient,
                            sourceApiConnectionDetails,
                            targetApiConnectionDetails)
                        .ConfigureAwait(false);
                }

                // Have all changes already been processed?
                if (changeWindow?.MinChangeVersion > changeWindow?.MaxChangeVersion)
                {
                    _logger.Info($"Last change version processed of '{GetLastChangeVersionProcessed(sourceApiConnectionDetails, targetApiConnectionDetails)}' for target API '{targetApiConnectionDetails.Name}' indicates that all available changes have already been published.");
                    return;
                }

                var postDependencyKeysByResourceKey = await PrepareResourceDependenciesAsync(
                        targetApiClient,
                        options,
                        authorizationFailureHandling,
                        sourceApiConnectionDetails)
                    .ConfigureAwait(false);

                // If we just wanted to know what resources are to be published, quit now.
                if (options.WhatIf)
                {
                    return;
                }
                
                // Create the global error processing block
                var (publishErrorsIngestionBlock, publishErrorsCompletionBlock) =
                    PublishErrors.GetBlocks(options, _errorPublisher);
                
                // Process all the key changes first
                var keyChangesTaskStatuses = await ProcessKeyChangesToCompletionAsync(
                    changeWindow,
                    postDependencyKeysByResourceKey,
                    configuration.ResourcesWithUpdatableKeys,
                    sourceApiClient, 
                    targetApiClient, 
                    options,
                    authorizationFailureHandling,
                    publishErrorsIngestionBlock,
                    cancellationToken)
                    .ConfigureAwait(false);
                
                // Process all the "Upserts"
                var postTaskStatuses = ProcessUpsertsToCompletion(
                    sourceApiClient, 
                    targetApiClient, 
                    postDependencyKeysByResourceKey, 
                    options,
                    authorizationFailureHandling,
                    changeWindow, 
                    publishErrorsIngestionBlock,
                    javascriptModuleFactory,
                    cancellationToken);

                // Process all the deletions
                var deleteTaskStatuses = await ProcessDeletesToCompletionAsync(
                    changeWindow, 
                    postDependencyKeysByResourceKey, 
                    sourceApiClient, 
                    targetApiClient, 
                    options,
                    authorizationFailureHandling,
                    publishErrorsIngestionBlock,
                    cancellationToken)
                    .ConfigureAwait(false);

                // Indicate to the error handling that we're done feeding it errors.
                publishErrorsIngestionBlock.Complete();
                
                // Wait for all errors to be published.
                _logger.Debug($"Waiting for all errors to be published.");
                publishErrorsCompletionBlock.Completion.Wait();

                EnsureProcessingWasSuccessful(keyChangesTaskStatuses, postTaskStatuses, deleteTaskStatuses);

                await UpdateChangeVersionAsync(configuration, changeWindow)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Fatal($"An unhandled exception occurred during processing: {ex}");
                throw;
            }
            finally
            {
                _logger.Info($"Processing finished in {processStopwatch.Elapsed.TotalSeconds:N0} seconds.");
            }
        }

        private async Task CheckApiVersionsAsync(ChangeProcessorConfiguration configuration)
        {
            var sourceApiClient = configuration.SourceApiClient;
            var targetApiClient = configuration.TargetApiClient;
            
            _logger.Debug($"Loading source and target API version information...");
            
            var sourceResponse = sourceApiClient.HttpClient.GetAsync("");
            var targetResponse = targetApiClient.HttpClient.GetAsync("");

            await Task.WhenAll(sourceResponse, targetResponse).ConfigureAwait(false);

            if (!sourceResponse.Result.IsSuccessStatusCode)
            {
                throw new Exception($"Source API at '{sourceApiClient.HttpClient.BaseAddress}' returned status code '{sourceResponse.Result.StatusCode}' for request for version information.");
            }
            
            if (!targetResponse.Result.IsSuccessStatusCode)
            {
                throw new Exception($"Target API at '{targetApiClient.HttpClient.BaseAddress}' returned status code '{targetResponse.Result.StatusCode}' for request for version information.");
            }
            
            string sourceJson = await sourceResponse.Result.Content.ReadAsStringAsync().ConfigureAwait(false);
            string targetJson = await targetResponse.Result.Content.ReadAsStringAsync().ConfigureAwait(false);

            var sourceVersionObject = GetVersionObject(sourceJson, "Source");
            var targetVersionObject = GetVersionObject(targetJson, "Target");

            string sourceApiVersionText = sourceVersionObject["version"].Value<string>();
            string targetApiVersionText = targetVersionObject["version"].Value<string>();

            var sourceApiVersion = new Version(sourceApiVersionText);
            var targetApiVersion = new Version(targetApiVersionText);

            // Apply resolved API version number to the runtime configuration
            configuration.SourceApiVersion = sourceApiVersion;
            configuration.TargetApiVersion = targetApiVersion;
            
            // Warn if API versions don't match
            if (!sourceApiVersion.Equals(targetApiVersion))
            {
                _logger.Warn($"Source API version {sourceApiVersion} and target API version {targetApiVersion} do not match.");
            }
            
            // Try comparing Ed-Fi versions
            if (sourceApiVersion.IsAtLeast(3, 1) && targetApiVersion.IsAtLeast(3, 1))
            {
                var sourceEdFiVersion = GetEdFiStandardVersion(sourceVersionObject);
                var targetEdFiVersion = GetEdFiStandardVersion(targetVersionObject);

                if (sourceEdFiVersion != targetEdFiVersion)
                {
                    _logger.Warn($"Source API is using Ed-Fi {sourceEdFiVersion} but target API is using Ed-Fi {targetEdFiVersion}. Some resources may not be publishable.");
                }
            }
            else
            {
                throw new NotSupportedException("The Ed-Fi API Publisher is not compatible with Ed-Fi ODS API versions prior to v3.1.");
                // Consider: _logger.Warn("Unable to verify Ed-Fi Standard versions between the source and target API since data model version information isn't available for one or both of the APIs.");
            }

            /*
             Sample responses:
             
            {
                version: "3.0.0",
                informationalVersion: "3.0",
                build: "3.0.0.2088",
                apiMode: "Sandbox"
            }    

            {
                version: "3.1.0",
                informationalVersion: "3.1",
                build: "3.1.0.3450",
                apiMode: "Sandbox"
            }
            
            {
                version: "3.1.1",
                informationalVersion: "3.1.1",
                build: "3.1.1.3888",
                apiMode: "Sandbox",
                dataModels: [
                    {
                        name: "Ed-Fi",
                        version: "3.1.0"
                    },
                    {
                        name: "GrandBend",
                        version: "1.0.0"
                    }
                ]
            }

            {
                version: "3.2.0",
                informationalVersion: "3.2.0",
                build: "3.2.0.4982",
                apiMode: "Sandbox",
                dataModels: [
                    {
                        name: "Ed-Fi",
                        version: "3.1.0"
                    },
                    {
                        name: "GrandBend",
                        version: "1.0.0"
                    }
                ]
            }
                     
            {
                version: "3.3.0",
                informationalVersion: "3.3.0-prerelease",
                build: "1.0.0.0",
                apiMode: "Sandbox",
                dataModels: [
                    {
                        name: "Ed-Fi",
                        version: "3.2.0"
                    }
                ]
            }
             */
            
            JObject GetVersionObject(string versionJson, string role)
            {
                JObject versionObject;

                try
                {
                    versionObject = JObject.Parse(versionJson);
                    _logger.Info($"{role} API version information: {versionObject.ToString(Formatting.Indented)}");
                }
                catch (Exception)
                {
                    throw new Exception($"Unable to parse version information returned from {role.ToLower()} API.");
                }

                return versionObject;
            }

            string GetEdFiStandardVersion(JObject jObject)
            {
                string edFiVersion;

                var dataModels = (JArray) jObject["dataModels"];

                edFiVersion = dataModels.Where(o => o["name"].Value<string>() == "Ed-Fi")
                    .Select(o => o["version"].Value<string>())
                    .SingleOrDefault();
                return edFiVersion;
            }
        }

        private async Task UpdateChangeVersionAsync(
            ChangeProcessorConfiguration configuration, 
            ChangeWindow changeWindow)
        {
            var sourceApiConnectionDetails = configuration.SourceApiConnectionDetails;
            var targetApiConnectionDetails = configuration.TargetApiConnectionDetails;
            var configurationStoreSection = configuration.ConfigurationStoreSection;
            
            // If we have a name for source and target connections, write the change version
            if (!string.IsNullOrEmpty(sourceApiConnectionDetails.Name)
                && !string.IsNullOrEmpty(targetApiConnectionDetails.Name))
            {
                if (changeWindow == null)
                {
                    _logger.Warn(
                        $"No change window was defined, so last processed change version for source connection '{sourceApiConnectionDetails.Name}' cannot be updated.");
                }
                else if (GetLastChangeVersionProcessed(sourceApiConnectionDetails, targetApiConnectionDetails) != changeWindow.MaxChangeVersion)
                {
                    _logger.Info(
                        $"Updating last processed change version from '{GetLastChangeVersionProcessed(sourceApiConnectionDetails, targetApiConnectionDetails)}' to '{changeWindow.MaxChangeVersion}' for target connection '{targetApiConnectionDetails.Name}'.");

                    // Record the successful completion of the change window
                    await _changeVersionProcessedWriter.SetProcessedChangeVersionAsync(
                        sourceApiConnectionDetails.Name,
                        targetApiConnectionDetails.Name,
                        changeWindow.MaxChangeVersion,
                        configurationStoreSection)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                if (changeWindow?.MaxChangeVersion != null && changeWindow.MaxChangeVersion != default(long))
                {
                    if (string.IsNullOrEmpty(sourceApiConnectionDetails.Name))
                    {
                        _logger.Info($"Unable to update the last change version processed because no connection name for source '{sourceApiConnectionDetails.Url}' was provided.");
                    }
                    else
                    {
                        _logger.Info($"Unable to update the last change version processed because no connection name for target '{targetApiConnectionDetails.Url}' was provided.");
                    }
                    
                    _logger.Info($"Last Change Version Processed for source '{sourceApiConnectionDetails.Url}' to target '{targetApiConnectionDetails.Url}': {changeWindow.MaxChangeVersion}");
                }
            }
        }

        private async Task<IDictionary<string, string[]>> PrepareResourceDependenciesAsync(
            EdFiApiClient targetApiClient, 
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling, 
            ApiConnectionDetails sourceApiConnectionDetails)
        {
            // Get the dependencies
            var postDependencyKeysByResourceKey = 
                await _resourceDependencyProvider.GetDependenciesByResourcePathAsync(
                    targetApiClient,
                    options.IncludeDescriptors)
                .ConfigureAwait(false);

            // Remove the Publishing extension, if present on target -- we don't want to publish snapshots
            // This logic is unnecessary starting with Ed-Fi ODS API v5.2
            postDependencyKeysByResourceKey.Remove("/publishing/snapshots");
            
            AdjustDependenciesForConfiguredAuthorizationConcerns();

            // Filter resources down to just those requested, if an explicit inclusion list provided
            if (!string.IsNullOrWhiteSpace(sourceApiConnectionDetails.Include) || !string.IsNullOrWhiteSpace(sourceApiConnectionDetails.IncludeOnly))
            {
                _logger.Info("Applying resource inclusions...");
                _logger.Debug($"Filtering processing to the following configured inclusion of source API resources:{Environment.NewLine}    Included (with dependencies):    {sourceApiConnectionDetails.Include}{Environment.NewLine}    Included (without dependencies): {sourceApiConnectionDetails.IncludeOnly}");

                var includeResourcePaths = ResourcePathHelper.ParseResourcesCsvToResourcePathArray(sourceApiConnectionDetails.Include);
                var includeOnlyResourcePaths = ResourcePathHelper.ParseResourcesCsvToResourcePathArray(sourceApiConnectionDetails.IncludeOnly);
                
                // Evaluate whether any of the included resources have a "retry" dependency
                var retryDependenciesForIncludeResourcePaths = includeResourcePaths
                    .Where(p => postDependencyKeysByResourceKey.ContainsKey($"{p}{Conventions.RetryKeySuffix}"))
                    .Select(p => $"{p}{Conventions.RetryKeySuffix}")
                    .ToArray();
                
                var retryDependenciesForIncludeOnlyResourcePaths = includeOnlyResourcePaths
                    .Where(p => postDependencyKeysByResourceKey.ContainsKey($"{p}{Conventions.RetryKeySuffix}"))
                    .Select(p => $"{p}{Conventions.RetryKeySuffix}")
                    .ToArray();
                
                postDependencyKeysByResourceKey = ApplyResourceInclusionsToDependencies(
                    postDependencyKeysByResourceKey,
                    includeResourcePaths.Concat(retryDependenciesForIncludeResourcePaths).ToArray(),
                    includeOnlyResourcePaths.Concat(retryDependenciesForIncludeOnlyResourcePaths).ToArray());

                // _logger.Info($"{postDependencyKeysByResourceKey.Count} resources to be processed after applying configuration for source API resource inclusion."); //"adding Ed-Fi dependencies.");
                //
                // var reportableResources = GetReportableResources();
                //
                // var resourceListMessage = $"The following resources are to be published:{Environment.NewLine}{string.Join(Environment.NewLine, reportableResources.Select(kvp => kvp.Key + string.Join(string.Empty, kvp.Value.Dependencies.Select(x => Environment.NewLine + "\t" + x))))}";
                //
                // if (options.WhatIf)
                // {
                //     _logger.Info(resourceListMessage);
                // }
                // else
                // {
                //     _logger.Debug(resourceListMessage);
                // }
            }
            
            if (!string.IsNullOrWhiteSpace(sourceApiConnectionDetails.Exclude) || !string.IsNullOrWhiteSpace(sourceApiConnectionDetails.ExcludeOnly))
            {
                _logger.Info("Applying resource exclusions...");
                _logger.Debug($"Filtering processing to the following configured exclusion of source API resources:{Environment.NewLine}    Excluded (along with dependents): {sourceApiConnectionDetails.Exclude}{Environment.NewLine}    Excluded (dependents unaffected): {sourceApiConnectionDetails.ExcludeOnly}");

                var excludeResourcePaths = ResourcePathHelper.ParseResourcesCsvToResourcePathArray(sourceApiConnectionDetails.Exclude);
                var excludeOnlyResourcePaths = ResourcePathHelper.ParseResourcesCsvToResourcePathArray(sourceApiConnectionDetails.ExcludeOnly);
                
                // Evaluate whether any of the included resources have a "retry" dependency
                var retryDependenciesForExcludeResourcePaths = excludeResourcePaths
                    .Where(p => postDependencyKeysByResourceKey.ContainsKey($"{p}{Conventions.RetryKeySuffix}"))
                    .Select(p => $"{p}{Conventions.RetryKeySuffix}")
                    .ToArray();

                var retryDependenciesForExcludeOnlyResourcePaths = excludeOnlyResourcePaths
                    .Where(p => postDependencyKeysByResourceKey.ContainsKey($"{p}{Conventions.RetryKeySuffix}"))
                    .Select(p => $"{p}{Conventions.RetryKeySuffix}")
                    .ToArray();
                
                postDependencyKeysByResourceKey = ApplyResourceExclusionsToDependencies(
                    postDependencyKeysByResourceKey,
                    excludeResourcePaths.Concat(retryDependenciesForExcludeResourcePaths).ToArray(),
                    excludeOnlyResourcePaths.Concat(retryDependenciesForExcludeOnlyResourcePaths).ToArray());

                // _logger.Info(
                //     $"{postDependencyKeysByResourceKey.Count} resources to be processed after removing dependent Ed-Fi resources.");
                //
                // var reportableResources = GetReportableResources();
                //
                // var resourceListMessage = $"The following resources are to be published:{Environment.NewLine}{string.Join(Environment.NewLine, reportableResources.Select(kvp => kvp.Key + string.Join(string.Empty, kvp.Value.Dependencies.Select(x => Environment.NewLine + "\t" + x))))}";
                //
                // if (options.WhatIf)
                // {
                //     _logger.Info(resourceListMessage);
                // }
                // else
                // {
                //     _logger.Debug(resourceListMessage);
                // }
            }
            
            // else
            // {
            //     // Was non-filtered list of resources to be published requested?
            //     if (options.WhatIf)
            //     {
            //         var reportableResources = GetReportableResources();
            //         var resourceListMessage = $"The following resources are to be published:{Environment.NewLine}{string.Join(Environment.NewLine, reportableResources.Select(kvp => kvp.Key + string.Join(string.Empty, kvp.Value.Dependencies.Select(x => Environment.NewLine + "\t" + x))))}";
            //         _logger.Info(resourceListMessage);
            //     }
            // }

            _logger.Info($"{postDependencyKeysByResourceKey.Count} resources to be processed after applying configuration for source API resource inclusions and/or exclusions.");
            
            var reportableResources = GetReportableResources();
            
            var resourceListMessage = $"The following resources are to be published:{Environment.NewLine}{string.Join(Environment.NewLine, reportableResources.Select(kvp => kvp.Key + string.Join(string.Empty, kvp.Value.Select(x => Environment.NewLine + "\t" + x))))}";
            
            // if (options.WhatIf)
            // {
                _logger.Info(resourceListMessage);
            // }
            // else
            // {
                // _logger.Debug(resourceListMessage);
            // }

            return postDependencyKeysByResourceKey;

            void AdjustDependenciesForConfiguredAuthorizationConcerns()
            {
                // Adjust dependencies for authorization failure handling metadata
                var dependencyAdjustments = authorizationFailureHandling
                    .Select(x => new
                    {
                        RetryResourceKey = $"{x.Path}{Conventions.RetryKeySuffix}",
                        UpdatePrerequisiteResourceKeys = x.UpdatePrerequisitePaths,
                        AffectedDependencyEntryKeys =
                            postDependencyKeysByResourceKey
                                .Where(kvp => kvp.Value.Contains(x.Path)
                                    && !x.UpdatePrerequisitePaths.Any(preReq => kvp.Key.Equals(preReq, StringComparison.Ordinal)))
                                .Select(kvp => kvp.Key)
                                .ToList()
                    })
                    .ToList();

                // Apply dependency adjustments
                foreach (var dependencyAdjustment in dependencyAdjustments)
                {
                    foreach (var dependencyEntryKey in dependencyAdjustment.AffectedDependencyEntryKeys)
                    {
                        postDependencyKeysByResourceKey[dependencyEntryKey] =
                            postDependencyKeysByResourceKey[dependencyEntryKey]
                                .Concat(new[] {dependencyAdjustment.RetryResourceKey})
                                .ToArray();
                    }

                    // Add retry dependencies to primary resource
                    postDependencyKeysByResourceKey[dependencyAdjustment.RetryResourceKey] =
                        dependencyAdjustment.UpdatePrerequisiteResourceKeys;
                }
            }

            IDictionary<string, string[]> ApplyResourceExclusionsToDependencies(
                IDictionary<string, string[]> dependenciesByResourcePath,
                string[] excludeResourcePaths, 
                string[] excludeOnlyResourcePaths)
            {
                var resourcesToInclude = new HashSet<string>(dependenciesByResourcePath.Keys, StringComparer.OrdinalIgnoreCase);
                var allExclusionTraceEntries = new List<string>();

                foreach (string excludedResourcePath in excludeResourcePaths)
                {
                    allExclusionTraceEntries.Add(
                        $"Excluding resource '{excludedResourcePath}' and its dependents...");
                    
                    var exclusionTraceEntries = new List<string>();
                    
                    RemoveDependentResources(excludedResourcePath, exclusionTraceEntries);

                    allExclusionTraceEntries.AddRange(exclusionTraceEntries.Distinct());
                }

                foreach (string excludeOnlyResourcePath in excludeOnlyResourcePaths)
                {
                    allExclusionTraceEntries.Add(
                        $"Excluding resource '{excludeOnlyResourcePath}' leaving dependents intact...");
                    
                    resourcesToInclude.Remove(excludeOnlyResourcePath);
                }
                
                var filteredResources = new Dictionary<string, string[]>(
                    dependenciesByResourcePath.Where(kvp => resourcesToInclude.Contains(kvp.Key)),
                    StringComparer.OrdinalIgnoreCase);

                // Remove dependencies on items that have not been included in publishing
                foreach (var resourceItem in filteredResources.ToArray())
                {
                    filteredResources[resourceItem.Key] =
                        resourceItem.Value.Where(dp => filteredResources.ContainsKey(dp)).ToArray();
                }

                if (_logger.IsDebugEnabled)
                {
                    if (allExclusionTraceEntries.Any())
                    {
                        _logger.Debug(
                            $"Resources exclusions were processed, as follows:{Environment.NewLine}{string.Join(Environment.NewLine, allExclusionTraceEntries)}");
                    }
                    else
                    {
                        _logger.Debug("No resources exclusions were applied.");
                    }
                }

                return filteredResources;

                void RemoveDependentResources(string resourcePath, List<string> exclusionTraceEntries)
                {
                    resourcesToInclude.Remove(resourcePath);

                    var dependentResourcePaths = dependenciesByResourcePath
                        .Where(kvp => kvp.Value.Contains(resourcePath))
                        .Select(kvp => kvp.Key);
                    
                    foreach (string dependentResourcePath in dependentResourcePaths)
                    {
                        if (!dependentResourcePath.EndsWith("Descriptors"))
                        {
                            exclusionTraceEntries.Add(
                                $"   Removing dependent '{dependentResourcePath}' of '{resourcePath}'...");
                        }

                        RemoveDependentResources(dependentResourcePath, exclusionTraceEntries);
                    }
                }
            }
            
            IDictionary<string, string[]> ApplyResourceInclusionsToDependencies(
                IDictionary<string, string[]> dependenciesByResourcePath,
                string[] includeResourcePaths, string[] includeOnlyResourcePaths)
            {
                var resourcesToInclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allInclusionTraceEntries = new List<string>();

                foreach (string includeResourcePath in includeResourcePaths)
                {
                    allInclusionTraceEntries.Add(
                        $"Including resource '{includeResourcePath}' and its dependencies...");

                    var inclusionTraceEntries = new List<string>();

                    AddDependencyResources(includeResourcePath, inclusionTraceEntries);

                    allInclusionTraceEntries.AddRange(inclusionTraceEntries.Distinct());
                }

                // Ensure the resources (without any additional dependencies) entries are also preserved
                foreach (string includeOnlyResourcePath in includeOnlyResourcePaths)
                {
                    allInclusionTraceEntries.Add($"Including resource '{includeOnlyResourcePath}' without its dependencies...");
                    
                    resourcesToInclude.Add(includeOnlyResourcePath);
                }

                var filteredResources = new Dictionary<string, string[]>(
                    dependenciesByResourcePath.Where(kvp => resourcesToInclude.Contains(kvp.Key)),
                        StringComparer.OrdinalIgnoreCase);

                // Remove dependencies on items that have not been included in publishing
                foreach (var resourceItem in filteredResources.ToArray())
                {
                    filteredResources[resourceItem.Key] =
                        resourceItem.Value.Where(dp => filteredResources.ContainsKey(dp)).ToArray();
                }
                
                if (_logger.IsDebugEnabled)
                {
                    if (allInclusionTraceEntries.Count > 0)
                    {
                        _logger.Debug(
                            $"Dependency resources were included, as follows:{Environment.NewLine}{string.Join(Environment.NewLine, allInclusionTraceEntries)}");
                    }
                    else
                    {
                        _logger.Debug("No dependency resources were included.");
                    }
                }

                return filteredResources;

                void AddDependencyResources(string resourcePath, List<string> inclusionTraceEntries)
                {
                    resourcesToInclude.Add(resourcePath);

                    foreach (string dependencyResourcePath in dependenciesByResourcePath[resourcePath])
                    {
                        if (!dependencyResourcePath.EndsWith("Descriptors"))
                        {
                            inclusionTraceEntries.Add(
                                $"   Adding dependency '{dependencyResourcePath}' of '{resourcePath}'...");
                        }

                        AddDependencyResources(dependencyResourcePath, inclusionTraceEntries);
                    }
                }
            }

            List<KeyValuePair<string, string[]>> GetReportableResources()
            {
                return postDependencyKeysByResourceKey
                    .Where(e => !e.Key.EndsWith("Descriptors") && !e.Key.EndsWith(Conventions.RetryKeySuffix))
                    .OrderBy(e => e.Key)
                    .ToList();
            }
        }

        private async Task ApplySourceSnapshotIdentifierAsync(
            EdFiApiClient sourceApiClient,
            ApiConnectionDetails sourceApiConnectionDetails,
            Version sourceApiVersion)
        {
            string snapshotIdentifier = await GetSourceSnapshotIdentifierAsync(sourceApiClient, sourceApiVersion).ConfigureAwait(false);

            // Confirm that a snapshot exists or --ignoreIsolation=true has been provided
            if (snapshotIdentifier == null)
            {
                string message = $"Snapshot identifier could not be obtained from API at '{sourceApiConnectionDetails.Url}', and \"force\" option was not specified. Publishing cannot proceed due to lack of guaranteed isolation from ongoing changes at the source. Use --ignoreIsolation=true (or a corresponding configuration value) to force processing.";
                throw new Exception(message);
            }
            else
            {
                // Configure source HTTP client to add the snapshot identifier header to every request against the source API
                sourceApiClient.HttpClient.DefaultRequestHeaders.Add("Snapshot-Identifier", snapshotIdentifier);
            }
        }

        private async Task<ChangeWindow> EstablishChangeWindow(
            EdFiApiClient sourceApiClient,
            ApiConnectionDetails sourceApiConnectionDetails,
            ApiConnectionDetails targetApiConnectionDetails)
        {
            // Get the current change version of the source database (or snapshot database)
            long? currentSourceChangeVersion = await GetCurrentSourceChangeVersionAsync(sourceApiClient).ConfigureAwait(false);

            // Establish change window
            ChangeWindow changeWindow = null;

            if (currentSourceChangeVersion.HasValue)
            {
                changeWindow = new ChangeWindow
                {
                    MinChangeVersion = 1 + GetLastChangeVersionProcessed(sourceApiConnectionDetails, targetApiConnectionDetails),
                    MaxChangeVersion = currentSourceChangeVersion.Value
                };
            }

            return changeWindow;
        }

        private static long GetLastChangeVersionProcessed(ApiConnectionDetails sourceApiConnectionDetails, ApiConnectionDetails targetApiConnectionDetails)
        {
            // If an explicit value was provided, use that first
            if (sourceApiConnectionDetails.LastChangeVersionProcessed.HasValue)
            {
                return sourceApiConnectionDetails.LastChangeVersionProcessed.Value;
            }
            
            // Fall back to using the pre-configured change version
            return sourceApiConnectionDetails
                .LastChangeVersionProcessedByTargetName
                .GetValueOrDefault(targetApiConnectionDetails.Name);
        }

        private TaskStatus[] ProcessUpsertsToCompletion(
            EdFiApiClient sourceApiClient,
            EdFiApiClient targetApiClient,
            IDictionary<string, string[]> postDependenciesByResourcePath,
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ChangeWindow changeWindow,
            ITargetBlock<ErrorItemMessage> errorPublishingBlock,
            Func<string>? javascriptModuleFactory,
            CancellationToken cancellationToken)
        {
            using var processingSemaphore = new SemaphoreSlim(
                options.MaxDegreeOfParallelismForResourceProcessing,
                options.MaxDegreeOfParallelismForResourceProcessing);

            // Start processing resources in dependency order
            var streamingPagesOfPostsByResourcePath = InitiateResourceStreaming(
                sourceApiClient,
                targetApiClient,
                postDependenciesByResourcePath,
                _postResourceBlocksFactory.CreateBlocks, 
                PostResourceBlocksFactory.CreateItemActionMessage,
                options,
                authorizationFailureHandling,
                changeWindow,
                errorPublishingBlock,
                processingSemaphore,
                javascriptModuleFactory,
                cancellationToken);

            // Wait for all upsert publishing to finish
            var postTaskStatuses = WaitForResourceStreamingToComplete(
                "upserts",
                streamingPagesOfPostsByResourcePath,
                processingSemaphore,
                options);
            
            return postTaskStatuses;
        }

        private async Task<TaskStatus[]> ProcessDeletesToCompletionAsync(
            ChangeWindow changeWindow,
            IDictionary<string, string[]> postDependenciesByResourcePath,
            EdFiApiClient sourceApiClient,
            EdFiApiClient targetApiClient,
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ITargetBlock<ErrorItemMessage> errorPublishingBlock,
            CancellationToken cancellationToken)
        {
            // Only process deletes if we are using a specific Change Window
            if (changeWindow == null)
            {
                _logger.Info($"No change window was defined, so no delete processing will be performed.");
                return Array.Empty<TaskStatus>();
            }

            if (changeWindow.MinChangeVersion <= 1)
            {
                _logger.Info($"Change window starting value indicates all values are being published, and so there is no need to perform delete processing.");
                return Array.Empty<TaskStatus>();
            }
            
            TaskStatus[] deleteTaskStatuses = Array.Empty<TaskStatus>();
            
            // Invert the dependencies for use in deletion, excluding descriptors (if present) and the special #Retry nodes
            var deleteDependenciesByResourcePath = InvertDependencies(postDependenciesByResourcePath, 
                path => path.EndsWith("Descriptors") || path.EndsWith(Conventions.RetryKeySuffix));

            if (deleteDependenciesByResourcePath.Any())
            {
                // Probe for deletes support
                string resourcePathSegment = deleteDependenciesByResourcePath.First().Key;
                string probeUrl = $"{sourceApiClient.DataManagementApiSegment}{resourcePathSegment}{EdFiApiConstants.DeletesPathSuffix}";

                _logger.Debug($"Probing source API for deletes support at '{probeUrl}'.");
                
                var probeResponse = await sourceApiClient.HttpClient.GetAsync($"{probeUrl}?limit=1").ConfigureAwait(false);

                if (probeResponse.IsSuccessStatusCode)
                {
                    _logger.Debug($"Probe response status was '{probeResponse.StatusCode}'. Initiating delete processing.");

                    using var processingSemaphore = new SemaphoreSlim(options.MaxDegreeOfParallelismForResourceProcessing, options.MaxDegreeOfParallelismForResourceProcessing);

                    var streamingPagesOfDeletesByResourcePath = InitiateResourceStreaming(
                        sourceApiClient,
                        targetApiClient,
                        deleteDependenciesByResourcePath,
                        DeleteResource.CreateBlocks, 
                        DeleteResource.CreateItemActionMessage,
                        options,
                        authorizationFailureHandling,
                        changeWindow,
                        errorPublishingBlock,
                        processingSemaphore,
                        null,
                        cancellationToken,
                        EdFiApiConstants.DeletesPathSuffix);

                    // Wait for everything to finish
                    deleteTaskStatuses = WaitForResourceStreamingToComplete(
                        "deletes",
                        streamingPagesOfDeletesByResourcePath,
                        processingSemaphore,
                        options);
                }
                else
                {
                    _logger.Warn($"Request to Source API for the '{EdFiApiConstants.DeletesPathSuffix}' child resource was unsuccessful (response status was '{probeResponse.StatusCode}'). Delete processing cannot be performed.");
                }
            }

            return deleteTaskStatuses;
        }
        
        private async Task<TaskStatus[]> ProcessKeyChangesToCompletionAsync(
            ChangeWindow changeWindow,
            IDictionary<string, string[]> postDependenciesByResourcePath,
            string[] resourcesWithUpdatableKeys,
            EdFiApiClient sourceApiClient,
            EdFiApiClient targetApiClient,
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ITargetBlock<ErrorItemMessage> errorPublishingBlock,
            CancellationToken cancellationToken)
        {
            // Only process key changes if we are using a specific Change Window
            if (changeWindow == null)
            {
                _logger.Info($"No change window was defined, so no key change processing will be performed.");
                return Array.Empty<TaskStatus>();
            }

            if (changeWindow.MinChangeVersion <= 1)
            {
                _logger.Info($"Change window starting value indicates all values are being published, and so there is no need to perform key change processing.");
                return Array.Empty<TaskStatus>();
            }
            
            TaskStatus[] keyChangeTaskStatuses = Array.Empty<TaskStatus>();
            
            if (resourcesWithUpdatableKeys.Any())
            {
                var keyChangeDependenciesByResourcePath = GetKeyChangeDependencies(postDependenciesByResourcePath, resourcesWithUpdatableKeys);

                // Probe for key changes support (using first by name sorted alphabetically for deterministic behavior)
                string resourcePathSegment = keyChangeDependenciesByResourcePath.Keys
                    .OrderBy(x => x)
                    .FirstOrDefault();

                if (resourcePathSegment == null)
                {
                    _logger.Info($"None of the API resources configured to allow key changes were found in the resources to be processed (based on dependency metadata retrieved from the target API).");

                    return Array.Empty<TaskStatus>();
                }

                string probeUrl = $"{sourceApiClient.DataManagementApiSegment}{resourcePathSegment}{EdFiApiConstants.KeyChangesPathSuffix}";

                _logger.Debug($"Probing source API for key changes support at '{probeUrl}'.");
                
                var probeResponse = await sourceApiClient.HttpClient.GetAsync($"{probeUrl}?limit=1").ConfigureAwait(false);

                if (probeResponse.IsSuccessStatusCode)
                {
                    _logger.Debug($"Probe response status was '{probeResponse.StatusCode}'. Initiating key changes processing.");

                    using var processingSemaphore = new SemaphoreSlim(
                        options.MaxDegreeOfParallelismForResourceProcessing,
                        options.MaxDegreeOfParallelismForResourceProcessing);

                    var streamingPagesOfKeyChangesByResourcePath = InitiateResourceStreaming(
                        sourceApiClient,
                        targetApiClient,
                        keyChangeDependenciesByResourcePath,
                        ChangeResourceKey.CreateBlocks, 
                        ChangeResourceKey.CreateItemActionMessage,
                        options,
                        authorizationFailureHandling,
                        changeWindow,
                        errorPublishingBlock,
                        processingSemaphore,
                        null,
                        cancellationToken,
                        EdFiApiConstants.KeyChangesPathSuffix);

                    // Wait for everything to finish
                    keyChangeTaskStatuses = WaitForResourceStreamingToComplete(
                        "key changes",
                        streamingPagesOfKeyChangesByResourcePath,
                        processingSemaphore,
                        options);
                }
                else
                {
                    _logger.Warn($"Request to Source API for the '{EdFiApiConstants.KeyChangesPathSuffix}' child resource was unsuccessful (response status was '{probeResponse.StatusCode}'). Key change processing cannot be performed.");
                }
            }

            return keyChangeTaskStatuses;
        }

        /// <summary>
        /// Gets a new dictionary of dependencies for processing that are limited to just the resources that can have their keys updated.
        /// </summary>
        /// <param name="postDependenciesByResourcePath"></param>
        /// <param name="resourcesWithUpdatableKeys"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private IDictionary<string, string[]> GetKeyChangeDependencies(IDictionary<string, string[]> postDependenciesByResourcePath, string[] resourcesWithUpdatableKeys)
        {
            // Copy the dependencies before modifying them
            var keyChangeDependenciesByResourcePath = new Dictionary<string, string[]>(postDependenciesByResourcePath);
            
            int infiniteLoopProtectionThreshold = keyChangeDependenciesByResourcePath.Count();
            int i = 0;
    
            while (i < infiniteLoopProtectionThreshold)
            {
                // Identify all resources that have no more dependencies (except retained dependencies)
                // (Retained dependencies are resources that have updatable keys and must be retained in the graph through flattening operations)
                var resourcesWithoutRetainedDependencies = keyChangeDependenciesByResourcePath
                    .Where(kvp => !kvp.Value.Except(resourcesWithUpdatableKeys).Any())
                    .Select(kvp => kvp.Key)
                    .Except(resourcesWithUpdatableKeys)
                    .ToArray();
        
                // Exit processing if there are no more resources that can be removed from the dependency graph
                if (!resourcesWithoutRetainedDependencies.Any())
                {
                    break;
                }
        
                var retainedDependenciesByResourcePath = new Dictionary<string, string[]>();
        
                // Iterate through all the resources that have no more dependencies (except retain dependencies)
                foreach (var resourcePathToBeRemoved in resourcesWithoutRetainedDependencies)
                {
                    var dependenciesWithUpdatableKeys = keyChangeDependenciesByResourcePath[resourcePathToBeRemoved]
                        .Intersect(resourcesWithUpdatableKeys)
                        .ToArray();
            
                    if (dependenciesWithUpdatableKeys.Any())
                    {
                        // Capture the dependencies of this resource that must be retained (used in place of dependencies on the resource being removed)
                        retainedDependenciesByResourcePath.Add(resourcePathToBeRemoved, dependenciesWithUpdatableKeys);
                    }
            
                    // Remove the resource from the graph
                    keyChangeDependenciesByResourcePath.Remove(resourcePathToBeRemoved);
                }
        
                // Iterate through the remaining graph resources
                foreach (var kvp in keyChangeDependenciesByResourcePath.ToArray())
                {
                    // Skip if the current resource doesn't have any dependencies on the resource that was just removed
                    if (!kvp.Value.Intersect(resourcesWithoutRetainedDependencies).Any())
                    {
                        continue;
                    }
            
                    // Identify dependencies of the current resource on any of the resources that were just removed that have retained dependencies (dependencies with updatable keys)
                    var dependenciesNeedingFlattening = retainedDependenciesByResourcePath.Keys.Intersect(kvp.Value).ToArray();
            
                    foreach (var dependencyToFlatten in dependenciesNeedingFlattening)
                    {
                        // Rebuild the array of dependencies...
                        keyChangeDependenciesByResourcePath[kvp.Key] = 
                            // Starting with all the current dependencies
                            kvp.Value
                            // Exclude the dependency for the resource that was removed from the graph
                            .Except(new[] { dependencyToFlatten })
                            // Add in that resource's retained dependencies here (flattening/simplifying the dependency graph for processing)
                            .Concat(retainedDependenciesByResourcePath[dependencyToFlatten])
                            .ToArray();
                    }
            
                    // Ensure all the dependencies for resources being removed have been removed
                    keyChangeDependenciesByResourcePath[kvp.Key] = kvp.Value.Except(resourcesWithoutRetainedDependencies).ToArray();
                }
        
                i++;
            }

            if (i == infiniteLoopProtectionThreshold)
            {
                // This should never happen
                throw new Exception(
                    "Unable to reduce resource dependencies for processing key changes as expected (the infinite loop threshold was exceeded during processing).");
            }

            return keyChangeDependenciesByResourcePath;
        }

        private void EnsureProcessingWasSuccessful(
            TaskStatus[] keyChangeTaskStatuses,
            TaskStatus[] postTaskStatuses,
            TaskStatus[] deleteTaskStatuses)
        {
            bool success = true;
            
            long publishedErrorCount = _errorPublisher.GetPublishedErrorCount();

            if (publishedErrorCount > 0)
            {
                success = false;
                _logger.Error($"{publishedErrorCount} unrecoverable errors occurred during resource item processing -- last change version will not be updated for this connection.");
            }

            var nonCompletedKeyChangeTaskCount = keyChangeTaskStatuses.Count(s => s != TaskStatus.RanToCompletion);

            if (nonCompletedKeyChangeTaskCount > 0)
            {
                success = false;
                _logger.Error($"{nonCompletedKeyChangeTaskCount} resource key change tasks did not run to completion successfully -- last change version processed will not be updated for this connection.");
            }

            var nonCompletedPostTaskCount = postTaskStatuses.Count(s => s != TaskStatus.RanToCompletion);

            if (nonCompletedPostTaskCount > 0)
            {
                success = false;
                _logger.Error($"{nonCompletedPostTaskCount} resource upsert tasks did not run to completion successfully -- last change version processed will not be updated for this connection.");
            }

            var nonCompletedDeleteTaskCount = deleteTaskStatuses.Count(s => s != TaskStatus.RanToCompletion);

            if (nonCompletedDeleteTaskCount > 0)
            {
                success = false;
                _logger.Error($"{nonCompletedDeleteTaskCount} resource delete tasks did not run to completion successfully -- last change version processed will not be updated for this connection.");
            }

            if (!success)
            {
                throw new Exception("Processing did not complete successfully.");
            }
        }

        /// <summary>
        /// Inverts the dependencies so that delete operations can be performed in the correct order.
        /// </summary>
        /// <param name="postDependenciesByResourcePath">The dependencies used for the POST operations.</param>
        /// <param name="excludeResourcePath">A delegate indicating whether a particular resource path should be excluded
        /// from the delete processing.</param>
        /// <returns>The dependencies for performing the delete operations.</returns>
        private IDictionary<string, string[]> InvertDependencies(
            IDictionary<string, string[]> postDependenciesByResourcePath,
            Func<string, bool> excludeResourcePath)
        {
            var reverseDependencyTuples =
                postDependenciesByResourcePath
                    .SelectMany(kvp => kvp.Value.Select(x => (x, kvp.Key)))
                    .Where(tuple => !excludeResourcePath(tuple.Key) && !excludeResourcePath(tuple.x));
                
            var allResourceTuples =
                postDependenciesByResourcePath
                    .Select(kvp => (kvp.Key, null as string))
                    .Where(tuple => !excludeResourcePath(tuple.Key));
                
            var deleteDependenciesByResourcePath =
                reverseDependencyTuples
                    .Concat(allResourceTuples)
                    .GroupBy(x => x.Item1)
                    .ToDictionary(
                        g => g.Key, 
                        g => g
                            .Where(x => !string.IsNullOrEmpty(x.Item2))
                            .Select(x => x.Item2)
                            .ToArray(), 
                        StringComparer.OrdinalIgnoreCase);
            
            return deleteDependenciesByResourcePath;
        }
        
        private async Task<string> GetSourceSnapshotIdentifierAsync(EdFiApiClient sourceApiClient, Version sourceApiVersion)
        {
            string snapshotsRelativePath;

            // Get available snapshot information
            if (sourceApiVersion.IsAtLeast(5, 2))
            {
                snapshotsRelativePath = $"{sourceApiClient.ChangeQueriesApiSegment}/snapshots";
            }
            else
            {
                snapshotsRelativePath = $"{sourceApiClient.DataManagementApiSegment}/publishing/snapshots";
            }
            
            var snapshotsResponse = await sourceApiClient.HttpClient.GetAsync(snapshotsRelativePath).ConfigureAwait(false);

            if (snapshotsResponse.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.Warn($"Source API at '{sourceApiClient.HttpClient.BaseAddress}' does not support the necessary isolation for reliable API publishing. Errors may occur, or some data may not be published without causing failures.");
                return null;
            }
            
            if (snapshotsResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.Warn($"The API publisher does not have permissions to access the source API's 'snapshots' resource at '{sourceApiClient.HttpClient.BaseAddress}{snapshotsRelativePath}'. Make sure that the source API is using a correctly configured claim set for your API Publisher's API client.");
                return null;
            }

            if (snapshotsResponse.IsSuccessStatusCode)
            {
                // Detect null content and provide a better error message (which happens during unit testing if mocked requests aren't properly defined)
                if (snapshotsResponse.Content == null)
                {
                    throw new NullReferenceException($"Content of response for '{sourceApiClient.HttpClient.BaseAddress}{snapshotsRelativePath}' was null.");
                }

                string snapshotResponseText = await snapshotsResponse.Content.ReadAsStringAsync()
                    .ConfigureAwait(false);

                var snapshotResponseArray = JArray.Parse(snapshotResponseText);

                if (!snapshotResponseArray.Any())
                {
                    // No snapshots available.
                    _logger.Warn($"Snapshots are supported, but no snapshots are available from source API at '{sourceApiClient.HttpClient.BaseAddress}{snapshotsRelativePath}'.");
                    return null;
                }
                
                var snapshot = 
                    snapshotResponseArray
                    .Select(jt =>
                    {
                        string snapshotIdentifier = jt["snapshotIdentifier"].Value<string>();
                        string snapshotDateTimeText = jt["snapshotDateTime"].Value<string>();

                        if (!DateTime.TryParse(snapshotDateTimeText, out var snapshotDateTimeValue))
                        {
                            snapshotDateTimeValue = DateTime.MinValue;
                        }
                        
                        return new
                        {
                            SnapshotIdentifier = snapshotIdentifier, 
                            SnapshotDateTime = snapshotDateTimeValue,
                            SnapshotDateTimeText = snapshotDateTimeText
                        };
                    })
                    .OrderByDescending(x => x.SnapshotDateTime)
                    .First();

                _logger.Info($"Using snapshot identifier '{snapshot.SnapshotIdentifier}' created at '{snapshot.SnapshotDateTime}'.");
                
                return snapshot.SnapshotIdentifier;
            }

            string errorResponseText = await snapshotsResponse.Content.ReadAsStringAsync()
                .ConfigureAwait(false);

            _logger.Error($"Unable to get snapshot identifier from API at '{sourceApiClient.HttpClient.BaseAddress}{snapshotsRelativePath}'. Request for available snapshots returned status '{snapshotsResponse.StatusCode}' with message body: {errorResponseText}");

            return null;
        }
        
        private async Task<long?> GetCurrentSourceChangeVersionAsync(EdFiApiClient sourceApiClient)
        {
            // Get current source version information
            string availableChangeVersionsRelativePath = $"{sourceApiClient.ChangeQueriesApiSegment}/availableChangeVersions";
            
            var versionResponse = await sourceApiClient.HttpClient.GetAsync(availableChangeVersionsRelativePath)
                .ConfigureAwait(false);

            if (!versionResponse.IsSuccessStatusCode)
            {
                _logger.Warn($"Unable to get current change version from source API at '{sourceApiClient.HttpClient.BaseAddress}{availableChangeVersionsRelativePath}' (response status: {versionResponse.StatusCode}). Full synchronization will always be performed against this source, and any concurrent changes made against the source may cause change processing to produce unreliable results.");
                return null;
            }
            
            string versionResponseText = await versionResponse.Content.ReadAsStringAsync()
                .ConfigureAwait(false);

            _logger.Debug(
                $"Available change versions request from {sourceApiClient.HttpClient.BaseAddress}{availableChangeVersionsRelativePath} returned {versionResponse.StatusCode}: {versionResponseText}");

            try
            {
                long maxChangeVersion = 
                    // Versions of Ed-Fi API through at least v3.4
                    (JObject.Parse(versionResponseText)["NewestChangeVersion"]
                        // Enhancements/fixes applied introduced as part of API Publisher work
                        ?? JObject.Parse(versionResponseText)["newestChangeVersion"])
                    .Value<long>();

                return maxChangeVersion;
            }
            catch (Exception ex)
            {
                throw new Exception($"Unable to read 'newestChangeVersion' property from response.", ex);
            }
        }

        private IDictionary<string, StreamingPagesItem> InitiateResourceStreaming<TItemActionMessage>(
            EdFiApiClient sourceApiClient,
            EdFiApiClient targetApiClient,
            IDictionary<string, string[]> dependenciesByResourcePath,
            Func<CreateBlocksRequest, (ITargetBlock<TItemActionMessage>,
                ISourceBlock<ErrorItemMessage>)> createProcessingBlocks,
            Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> createItemActionMessage,
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ChangeWindow changeWindow,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            SemaphoreSlim processingSemaphore,
            Func<string>? javascriptModuleFactory,
            CancellationToken cancellationToken,
            string resourceUrlPathSuffix = null)
        {
            _logger.Info($"Initiating resource streaming.");

            var linkOptions = new DataflowLinkOptions {PropagateCompletion = true};

            var streamingPagesByResourceKey = new Dictionary<string, StreamingPagesItem>(StringComparer.OrdinalIgnoreCase);

            var streamingResourceBlockByResourceKey = new Dictionary<string, ITargetBlock<StreamResourceMessage>>(StringComparer.OrdinalIgnoreCase);
            
            var postAuthorizationRetryByResourceKey = new Dictionary<string, Action<object>>(StringComparer.OrdinalIgnoreCase);

            // Set up streaming resource blocks for all resources
            foreach (var kvp in dependenciesByResourcePath)
            {
                string resourceKey = kvp.Key;
                string resourcePath = ResourcePathHelper.GetResourcePath(resourceKey);

                var createBlocksRequest = new CreateBlocksRequest(
                    sourceApiClient,
                    targetApiClient,
                    options,
                    authorizationFailureHandling,
                    errorHandlingBlock,
                    javascriptModuleFactory);
                
                var (processingInBlock, processingOutBlock) = createProcessingBlocks(createBlocksRequest);

                // Is this an authorization retry "resource"? 
                if (resourceKey.EndsWith(Conventions.RetryKeySuffix))
                {
                    // Save the action delegate, keyed by the main resource
                    postAuthorizationRetryByResourceKey.Add(
                        resourcePath, 
                        msg => processingInBlock.Post((TItemActionMessage) msg));
                }

                streamingPagesByResourceKey.Add(
                    resourceKey,
                    new StreamingPagesItem
                    {
                        CompletionBlock = processingOutBlock
                    });

                var streamResourceBlock = StreamResource.CreateBlock(createItemActionMessage, errorHandlingBlock, options, cancellationToken);
                var streamResourcePagesBlock = StreamResourcePages.GetBlock<TItemActionMessage>(options, errorHandlingBlock);

                streamResourceBlock.LinkTo(streamResourcePagesBlock, linkOptions);
                streamResourcePagesBlock.LinkTo(processingInBlock, linkOptions);
                processingOutBlock.LinkTo(errorHandlingBlock, new DataflowLinkOptions { Append = true });

                streamingResourceBlockByResourceKey.Add(resourceKey, streamResourceBlock);
            }
            
            var cancellationSource = new CancellationTokenSource();

            // Initiate streaming of all resources, with dependencies
            foreach (var kvp in dependenciesByResourcePath)
            {
                var resourceKey = kvp.Key;
                var resourcePath = ResourcePathHelper.GetResourcePath(resourceKey);
                var dependencyPaths = kvp.Value.ToArray();

                string resourceUrl = $"{resourcePath}{resourceUrlPathSuffix}";

                if (cancellationSource.IsCancellationRequested)
                {
                    _logger.Debug($"{resourceUrl}: Cancellation requested -- resource will not be streamed.");
                    break;
                }

                // Record the dependencies for status reporting
                streamingPagesByResourceKey[resourceKey].DependencyPaths = dependencyPaths;

                postAuthorizationRetryByResourceKey.TryGetValue(resourceKey, out var postRetry);

                var skippedResources = 
                    ResourcePathHelper.ParseResourcesCsvToResourcePathArray(sourceApiClient.ConnectionDetails.ExcludeOnly);

                var message = new StreamResourceMessage
                {
                    EdFiApiClient = sourceApiClient,
                    ResourceUrl = resourceUrl,
                    ShouldSkip = skippedResources.Contains(resourcePath),
                    Dependencies = dependencyPaths.Select(p => streamingPagesByResourceKey[p].CompletionBlock.Completion).ToArray(),
                    DependencyPaths = dependencyPaths.ToArray(),
                    PageSize = options.StreamingPageSize,
                    ChangeWindow = changeWindow, 
                    CancellationSource = cancellationSource,
                    PostAuthorizationFailureRetry = postRetry,
                    ProcessingSemaphore = processingSemaphore,
                };

                if (postRetry != null)
                {
                    _logger.Debug($"{message.ResourceUrl}: Authorization retry processing is supported.");
                }

                var streamingBlock = streamingResourceBlockByResourceKey[resourceKey];

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"{message.ResourceUrl}: Sending message to initiate streaming.");
                }

                streamingBlock.Post(message);
                streamingBlock.Complete();
            }

            return streamingPagesByResourceKey;
        }

        private TaskStatus[] WaitForResourceStreamingToComplete(
            string activityDescription,
            IDictionary<string, StreamingPagesItem> streamingPagesByResourcePath,
            SemaphoreSlim processingSemaphore,
            Options options)
        {
            var completedStreamingPagesByResourcePath = new Dictionary<string, StreamingPagesItem>(StringComparer.OrdinalIgnoreCase);

            _logger.Info($"Waiting for {streamingPagesByResourcePath.Count} {activityDescription} streaming sources to complete...");

            var lastProgressUpdate = DateTime.Now;
            
            while (streamingPagesByResourcePath.Any())
            {
                string[] resourcePaths = streamingPagesByResourcePath.Keys.OrderBy(x => x).ToArray();

                if (DateTime.Now - lastProgressUpdate > TimeSpan.FromSeconds(options.StreamingPagesWaitDurationSeconds))
                {
                    int paddedDisplayLength = resourcePaths.Max(x => x.Length) + 2;

                    var remainingResources = resourcePaths.Select(rp => new
                        {
                            ResourcePath = rp, 
                            DependentItems = streamingPagesByResourcePath[rp].DependencyPaths.Where(streamingPagesByResourcePath.ContainsKey).ToArray()
                        })
                        .Select(x => new
                        {
                            ResourcePath = x.ResourcePath,
                            DependentItems = x.DependentItems,
                            Count = x.DependentItems.Length
                        }).ToArray();

                    var resourcesBeingProcessed = remainingResources
                        .Where(x => x.Count == 0)
                        .OrderBy(x => x.ResourcePath)
                        .ToArray();
                    
                    var itemsMessage = new StringBuilder();

                    itemsMessage.AppendLine($"The following {resourcesBeingProcessed.Length} resources are processing (or ready for processing):");
                    
                    foreach (var resource in resourcesBeingProcessed)
                    {
                        itemsMessage.Append("    ");
                        itemsMessage.AppendLine($"{GetResourcePathDisplayText(resource.ResourcePath)}");
                    }
                    
                    var resourcesWaiting = remainingResources
                        .Where(x => x.Count > 0)
                        .OrderBy(x => x.ResourcePath)
                        .ToArray();

                    if (resourcesWaiting.Length > 0)
                    {
                        itemsMessage.AppendLine();
                        itemsMessage.AppendLine($"The following {resourcesWaiting.Length} resources are waiting for dependencies to complete:");

                        foreach (var resource in resourcesWaiting)
                        {
                            itemsMessage.Append("    ");
                            itemsMessage.AppendLine($"{GetResourcePathDisplayText(resource.ResourcePath, paddedDisplayLength)} ({resource.DependentItems.Length} dependencies remaining --> {Truncate(string.Join(", ", resource.DependentItems.Select(x => GetResourcePathDisplayText(x))), 50)})");
                        }
                    }
                    
                    string Truncate(string text, int length)
                    {
                        if (text.Length <= length)
                        {
                            return text;
                        }

                        return text.Substring(0, length) + "...";
                    }

                    string GetResourcePathDisplayText(string resourcePath, int padToLength = 0)
                    {
                        string[] parts = resourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

                        string displayName;
                        
                        if (parts[0] == "ed-fi")
                        {
                            displayName = parts[1];
                        }
                        else
                        {
                            displayName = $"{parts[1]} ({parts[0]})";
                        }

                        if (padToLength == 0)
                        {
                            return displayName;
                        }
                        
                        return $"{displayName}{new string(' ', (padToLength + 2) - displayName.Length)}";
                    }
                    
                    string logMessage = $"Waiting for the {activityDescription} streaming of {resourcePaths.Length} resources to complete...{Environment.NewLine}{itemsMessage}";
                    
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug(logMessage);
                    }
                    else 
                    {
                        _logger.Info(logMessage);
                    }
                    
                    lastProgressUpdate = DateTime.Now;
                }
                
                int completedIndex = Task.WaitAny(
                    resourcePaths.Select(k => streamingPagesByResourcePath[k].CompletionBlock.Completion).ToArray(),
                    TimeSpan.FromSeconds(options.StreamingPagesWaitDurationSeconds));

                if (completedIndex >= 0)
                {
                    // Check for unhandled task failure
                    var resourcePath = resourcePaths.ElementAt(completedIndex);
                    var streamingPagesItem = streamingPagesByResourcePath[resourcePath];
                    
                    var blockCompletion = streamingPagesItem.CompletionBlock.Completion;
                    
                    if (blockCompletion.IsFaulted)
                    {
                        _logger.Fatal($"Streaming task failure for {resourcePath}: {blockCompletion.Exception}");
                    }
                    
                    completedStreamingPagesByResourcePath.Add(resourcePaths[completedIndex],
                        streamingPagesByResourcePath[resourcePaths[completedIndex]]);

                    streamingPagesByResourcePath.Remove(resourcePaths[completedIndex]);

                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"Streaming of '{resourcePaths[completedIndex]}' completed. Releasing a processing slot ({processingSemaphore.CurrentCount} slots currently available)...");
                    }

                    _logger.Info($"Streaming of '{resourcePaths[completedIndex]}' completed. {streamingPagesByResourcePath.Count} resource(s) remaining. {processingSemaphore.CurrentCount + 1} processing slot(s) soon to be available.");

                    try
                    {
                        processingSemaphore.Release();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Attempt to release the semaphore resulted in an exception: {ex}");
                    }
                }
            }

            return completedStreamingPagesByResourcePath
                .Select(kvp => kvp.Value.CompletionBlock.Completion.Status)
                .ToArray();
        }
    }
}