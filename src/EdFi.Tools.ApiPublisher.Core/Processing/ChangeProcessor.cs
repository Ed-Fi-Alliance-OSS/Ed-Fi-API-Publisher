// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac.Features.Indexed;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Finalization;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Isolation;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Version = EdFi.Tools.ApiPublisher.Core.Helpers.Version;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    public enum PublishingStage
    {
        KeyChanges,
        Upserts,
        Deletes
    }
    
    public class ChangeProcessor
    {
        private ILogger _logger = Log.ForContext(typeof(ChangeProcessor));

        private readonly IResourceDependencyProvider _resourceDependencyProvider;
        private readonly IChangeVersionProcessedWriter _changeVersionProcessedWriter;
        private readonly IErrorPublisher _errorPublisher;
        private readonly IEdFiVersionsChecker _edFiVersionsChecker;
        private readonly ISourceCurrentChangeVersionProvider _sourceCurrentChangeVersionProvider;
        private readonly ISourceConnectionDetails _sourceConnectionDetails;
        private readonly ITargetConnectionDetails _targetConnectionDetails;
        private readonly ISourceIsolationApplicator _sourceIsolationApplicator;
        private readonly ISourceCapabilities _sourceCapabilities;
        private readonly PublishErrorsBlocksFactory _publishErrorsBlocksFactory;
        private readonly IIndex<PublishingStage, IPublishingStageInitiator> _publishingStageInitiatorByStage;
        private readonly IFinalizationActivity[] _finalizationActivities;

        public ChangeProcessor(
            IResourceDependencyProvider resourceDependencyProvider,
            IChangeVersionProcessedWriter changeVersionProcessedWriter,
            IErrorPublisher errorPublisher,
            IEdFiVersionsChecker edFiVersionsChecker,
            ISourceCurrentChangeVersionProvider sourceCurrentChangeVersionProvider,
            ISourceConnectionDetails sourceConnectionDetails,
            ITargetConnectionDetails targetConnectionDetails,
            ISourceIsolationApplicator sourceIsolationApplicator,
            ISourceCapabilities sourceCapabilities,
            PublishErrorsBlocksFactory publishErrorsBlocksFactory,
            IIndex<PublishingStage, IPublishingStageInitiator> publishingStageInitiatorByStage,
            IFinalizationActivity[] finalizationActivities)
        {
            _resourceDependencyProvider = resourceDependencyProvider;
            _changeVersionProcessedWriter = changeVersionProcessedWriter;
            _errorPublisher = errorPublisher;
            _edFiVersionsChecker = edFiVersionsChecker;
            _sourceCurrentChangeVersionProvider = sourceCurrentChangeVersionProvider;
            _sourceConnectionDetails = sourceConnectionDetails;
            _targetConnectionDetails = targetConnectionDetails;
            _sourceIsolationApplicator = sourceIsolationApplicator;
            _sourceCapabilities = sourceCapabilities;
            _publishErrorsBlocksFactory = publishErrorsBlocksFactory;
            _publishingStageInitiatorByStage = publishingStageInitiatorByStage;
            _finalizationActivities = finalizationActivities;
        }
        
        public async Task ProcessChangesAsync(ChangeProcessorConfiguration configuration, CancellationToken cancellationToken)
        {
            var processStopwatch = new Stopwatch();
            processStopwatch.Start();
            
            var authorizationFailureHandling= configuration.AuthorizationFailureHandling;
            var options = configuration.Options;
            var javascriptModuleFactory = configuration.JavascriptModuleFactory;

            _logger.Debug($"Options for processing:{Environment.NewLine}{JsonConvert.SerializeObject(options, Formatting.Indented)}");

            try
            {
                // Check Ed-Fi API and Standard versions for compatibility
                await _edFiVersionsChecker.CheckApiVersionsAsync(configuration).ConfigureAwait(false);

                // Look for and apply snapshots if flag was not provided
                if (!(_sourceConnectionDetails.IgnoreIsolation ?? false))
                {
                    // Determine if source API provides a snapshot, and apply the HTTP header to the client 
                    await _sourceIsolationApplicator.ApplySourceSnapshotIdentifierAsync(configuration.SourceApiVersion)
                        .ConfigureAwait(false);
                }

                // Establish the change window we're processing, if any.
                ChangeWindow changeWindow = null;

                // Only named (managed) connections can use a Change Window for processing.
                if ((!string.IsNullOrWhiteSpace(_sourceConnectionDetails.Name) 
                    && !string.IsNullOrWhiteSpace(_targetConnectionDetails.Name))
                    || options.UseChangeVersionPaging)
                {
                    changeWindow = await EstablishChangeWindowAsync().ConfigureAwait(false);
                }

                // Have all changes already been processed?
                if (changeWindow?.MinChangeVersion > changeWindow?.MaxChangeVersion)
                {
                    _logger.Information($"Last change version processed of '{GetLastChangeVersionProcessed()}' for target '{_targetConnectionDetails.Name}' indicates that all available changes have already been published.");
                    return;
                }

                var dependencyKeysByResourceKey = await PrepareResourceDependenciesAsync(
                        options,
                        authorizationFailureHandling)
                    .ConfigureAwait(false);

                // If we just wanted to know what resources are to be published, quit now.
                if (options.WhatIf)
                {
                    return;
                }
                
                // Create the shared error processing block
                var (publishErrorsIngestionBlock, publishErrorsCompletionBlock) = _publishErrorsBlocksFactory.CreateBlocks(options);

                // Process all the key changes first
                var keyChangesTaskStatuses = await ProcessKeyChangesToCompletionAsync(
                    changeWindow,
                    dependencyKeysByResourceKey,
                    configuration.ResourcesWithUpdatableKeys,
                    options,
                    authorizationFailureHandling,
                    publishErrorsIngestionBlock,
                    cancellationToken)
                    .ConfigureAwait(false);
                
                // Process all the "Upserts"
                var postTaskStatuses = ProcessUpsertsToCompletion(
                    dependencyKeysByResourceKey, 
                    options,
                    authorizationFailureHandling,
                    changeWindow, 
                    publishErrorsIngestionBlock,
                    javascriptModuleFactory,
                    cancellationToken);

                // Process all the deletions
                var deleteTaskStatuses = await ProcessDeletesToCompletionAsync(
                    changeWindow, 
                    dependencyKeysByResourceKey, 
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

                // Perform processing finalization activities
                var finalizationTasks = _finalizationActivities.Select(f => f.Execute()).ToArray();

                try
                {
                    await Task.WhenAll(finalizationTasks);
                }
                catch (Exception)
                {
                    _logger.Error("A finalization task failed. The last processed ChangeVersion will not be updated.");
                    throw;
                }

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
                _logger.Information($"Processing finished in {processStopwatch.Elapsed.TotalSeconds:N0} seconds.");
            }
        }

        private async Task UpdateChangeVersionAsync(
            ChangeProcessorConfiguration configuration, 
            ChangeWindow changeWindow)
        {
            var sourceDetails = _sourceConnectionDetails;
            var sinkDetails = _targetConnectionDetails;
            
            var configurationStoreSection = configuration.ConfigurationStoreSection;
            
            // If we have a name for source and target connections, write the change version
            if (!string.IsNullOrEmpty(sourceDetails.Name)
                && !string.IsNullOrEmpty(sinkDetails.Name))
            {
                if (changeWindow == null)
                {
                    _logger.Warning(
                        $"No change window was defined, so last processed change version for source connection '{sourceDetails.Name}' cannot be updated.");
                }
                else if (GetLastChangeVersionProcessed() != changeWindow.MaxChangeVersion)
                {
                    _logger.Information(
                        $"Updating last processed change version from '{GetLastChangeVersionProcessed()}' to '{changeWindow.MaxChangeVersion}' for target connection '{sinkDetails.Name}'.");

                    // Record the successful completion of the change window
                    await _changeVersionProcessedWriter.SetProcessedChangeVersionAsync(
                        sourceDetails.Name,
                        sinkDetails.Name,
                        changeWindow.MaxChangeVersion,
                        configurationStoreSection)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                if (changeWindow?.MaxChangeVersion != null && changeWindow.MaxChangeVersion != default)
                {
                    if (string.IsNullOrEmpty(sourceDetails.Name))
                    {
                        _logger.Information($"Unable to update the last change version processed because no name was provided for the source.");
                    }
                    else
                    {
                        _logger.Information($"Unable to update the last change version processed because no name was provided for the target.");
                    }
                    
                    _logger.Information($"Last Change Version Processed for source to target: {changeWindow.MaxChangeVersion}");
                }
            }
        }

        private async Task<IDictionary<string, string[]>> PrepareResourceDependenciesAsync(
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling)
        {
            // Get the dependencies
            var postDependencyKeysByResourceKey = 
                await _resourceDependencyProvider.GetDependenciesByResourcePathAsync(options.IncludeDescriptors)
                .ConfigureAwait(false);

            // Ensure the Publishing extension is not present in dependencies -- we don't want to publish snapshots as a resource
            // NOTE: This logic is unnecessary starting with Ed-Fi ODS API v5.2
            postDependencyKeysByResourceKey.Remove("/publishing/snapshots");
            
            AdjustDependenciesForConfiguredAuthorizationConcerns();

            // Filter resources down to just those requested, if an explicit inclusion list provided
            if (!string.IsNullOrWhiteSpace(_sourceConnectionDetails.Include) || !string.IsNullOrWhiteSpace(_sourceConnectionDetails.IncludeOnly))
            {
                _logger.Information("Applying resource inclusions...");
                _logger.Debug($"Filtering processing to the following configured inclusion of source API resources:{Environment.NewLine}    Included (with dependencies):    {_sourceConnectionDetails.Include}{Environment.NewLine}    Included (without dependencies): {_sourceConnectionDetails.IncludeOnly}");

                var includeResourcePaths = ResourcePathHelper.ParseResourcesCsvToResourcePathArray(_sourceConnectionDetails.Include);
                var includeOnlyResourcePaths = ResourcePathHelper.ParseResourcesCsvToResourcePathArray(_sourceConnectionDetails.IncludeOnly);
                
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

                // _logger.Information($"{postDependencyKeysByResourceKey.Count} resources to be processed after applying configuration for source API resource inclusion."); //"adding Ed-Fi dependencies.");
                //
                // var reportableResources = GetReportableResources();
                //
                // var resourceListMessage = $"The following resources are to be published:{Environment.NewLine}{string.Join(Environment.NewLine, reportableResources.Select(kvp => kvp.Key + string.Join(string.Empty, kvp.Value.Dependencies.Select(x => Environment.NewLine + "\t" + x))))}";
                //
                // if (options.WhatIf)
                // {
                //     _logger.Information(resourceListMessage);
                // }
                // else
                // {
                //     _logger.Debug(resourceListMessage);
                // }
            }
            
            if (!string.IsNullOrWhiteSpace(_sourceConnectionDetails.Exclude) || !string.IsNullOrWhiteSpace(_sourceConnectionDetails.ExcludeOnly))
            {
                _logger.Information("Applying resource exclusions...");
                _logger.Debug($"Filtering processing to the following configured exclusion of source API resources:{Environment.NewLine}    Excluded (along with dependents): {_sourceConnectionDetails.Exclude}{Environment.NewLine}    Excluded (dependents unaffected): {_sourceConnectionDetails.ExcludeOnly}");

                var excludeResourcePaths = ResourcePathHelper.ParseResourcesCsvToResourcePathArray(_sourceConnectionDetails.Exclude);
                var excludeOnlyResourcePaths = ResourcePathHelper.ParseResourcesCsvToResourcePathArray(_sourceConnectionDetails.ExcludeOnly);
                
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

                // _logger.Information(
                //     $"{postDependencyKeysByResourceKey.Count} resources to be processed after removing dependent Ed-Fi resources.");
                //
                // var reportableResources = GetReportableResources();
                //
                // var resourceListMessage = $"The following resources are to be published:{Environment.NewLine}{string.Join(Environment.NewLine, reportableResources.Select(kvp => kvp.Key + string.Join(string.Empty, kvp.Value.Dependencies.Select(x => Environment.NewLine + "\t" + x))))}";
                //
                // if (options.WhatIf)
                // {
                //     _logger.Information(resourceListMessage);
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
            //         _logger.Information(resourceListMessage);
            //     }
            // }

            _logger.Information($"{postDependencyKeysByResourceKey.Count} resources to be processed after applying configuration for source API resource inclusions and/or exclusions.");
            
            var reportableResources = GetReportableResources();
            
            var resourceListMessage = $"The following resources are to be published:{Environment.NewLine}{string.Join(Environment.NewLine, reportableResources.Select(kvp => kvp.Key + string.Join(string.Empty, kvp.Value.Select(x => Environment.NewLine + "\t" + x))))}";
            
            // if (options.WhatIf)
            // {
                _logger.Information(resourceListMessage);
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

                if (_logger.IsEnabled(LogEventLevel.Debug))
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
                
                if (_logger.IsEnabled(LogEventLevel.Debug))
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

        private async Task<ChangeWindow> EstablishChangeWindowAsync()
        {
            // Get the current change version of the source database (or snapshot database)
            long? currentSourceChangeVersion =
                await _sourceCurrentChangeVersionProvider.GetCurrentChangeVersionAsync().ConfigureAwait(false);

            // Establish change window
            ChangeWindow changeWindow = null;

            if (currentSourceChangeVersion.HasValue)
            {
                changeWindow = new ChangeWindow
                {
                    MinChangeVersion = 1 + GetLastChangeVersionProcessed(),
                    MaxChangeVersion = currentSourceChangeVersion.Value
                };
            }

            return changeWindow;
        }

        private long GetLastChangeVersionProcessed()
        {
            // If an explicit value was provided, use that first
            if (_sourceConnectionDetails.LastChangeVersionProcessed.HasValue)
            {
                return _sourceConnectionDetails.LastChangeVersionProcessed.Value;
            }
            
            // Fall back to using the pre-configured change version
            return _sourceConnectionDetails
                .LastChangeVersionProcessedByTargetName
                .GetValueOrDefault(_targetConnectionDetails.Name);
        }

        private TaskStatus[] ProcessUpsertsToCompletion(
            IDictionary<string, string[]> postDependenciesByResourcePath,
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ChangeWindow changeWindow,
            ITargetBlock<ErrorItemMessage> errorPublishingBlock,
            Func<string> javascriptModuleFactory,
            CancellationToken cancellationToken)
        {
            using var processingSemaphore = new SemaphoreSlim(
                options.MaxDegreeOfParallelismForResourceProcessing,
                options.MaxDegreeOfParallelismForResourceProcessing);

            var initiator = _publishingStageInitiatorByStage[PublishingStage.Upserts];

            var processingContext = new ProcessingContext(
                changeWindow,
                postDependenciesByResourcePath,
                errorPublishingBlock,
                processingSemaphore,
                options,
                authorizationFailureHandling,
                Array.Empty<string>(),
                javascriptModuleFactory,
                null);
            
            // Start processing resources in dependency order
            var streamingPagesOfPostsByResourcePath = initiator.Start(processingContext, cancellationToken);

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
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ITargetBlock<ErrorItemMessage> errorPublishingBlock,
            CancellationToken cancellationToken)
        {
            // Only process deletes if we are using a specific Change Window
            if (changeWindow == null)
            {
                _logger.Information($"No change window was defined, so no delete processing will be performed.");
                return Array.Empty<TaskStatus>();
            }

            if (changeWindow.MinChangeVersion <= 1)
            {
                _logger.Information($"Change window starting value indicates all values are being published, and so there is no need to perform delete processing.");
                return Array.Empty<TaskStatus>();
            }
            
            TaskStatus[] deleteTaskStatuses = Array.Empty<TaskStatus>();
            
            // Invert the dependencies for use in deletion, excluding descriptors (if present) and the special #Retry nodes
            var deleteDependenciesByResourcePath = InvertDependencies(postDependenciesByResourcePath, 
                path => path.EndsWith("Descriptors") || path.EndsWith(Conventions.RetryKeySuffix));

            if (deleteDependenciesByResourcePath.Any())
            {
                // Probe for deletes support
                string probeResourceKey = deleteDependenciesByResourcePath.First().Key;
                var supportsDeletes = await _sourceCapabilities.SupportsDeletesAsync(probeResourceKey);

                if (supportsDeletes)
                {
                    // _logger.Debug($"Probe response status was '{probeResponse.StatusCode}'. Initiating delete processing.");

                    using var processingSemaphore = new SemaphoreSlim(
                        options.MaxDegreeOfParallelismForResourceProcessing,
                        options.MaxDegreeOfParallelismForResourceProcessing);

                    var processingContext = new ProcessingContext(
                        changeWindow,
                        deleteDependenciesByResourcePath,
                        errorPublishingBlock,
                        processingSemaphore,
                        options,
                        authorizationFailureHandling,
                        Array.Empty<string>(),
                        null,
                        EdFiApiConstants.DeletesPathSuffix);

                    var initiator = _publishingStageInitiatorByStage[PublishingStage.Deletes];

                    var streamingPagesOfDeletesByResourcePath = initiator.Start(processingContext, cancellationToken);

                    // Wait for everything to finish
                    deleteTaskStatuses = WaitForResourceStreamingToComplete(
                        "deletes",
                        streamingPagesOfDeletesByResourcePath,
                        processingSemaphore,
                        options);
                }
                else
                {
                    _logger.Warning($"Source indicated a lack of support for deletes. Delete processing cannot be performed.");
                }
            }

            return deleteTaskStatuses;
        }

        private async Task<TaskStatus[]> ProcessKeyChangesToCompletionAsync(
            ChangeWindow changeWindow,
            IDictionary<string, string[]> dependencyKeysByResourceKey,
            string[] resourcesWithUpdatableKeys,
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ITargetBlock<ErrorItemMessage> errorPublishingBlock,
            CancellationToken cancellationToken)
        {
            // Only process key changes if we are using a specific Change Window
            if (changeWindow == null)
            {
                _logger.Information($"No change window was defined, so no key change processing will be performed.");
                return Array.Empty<TaskStatus>();
            }

            if (changeWindow.MinChangeVersion <= 1)
            {
                _logger.Information($"Change window starting value indicates all values are being published, and so there is no need to perform key change processing.");
                return Array.Empty<TaskStatus>();
            }

            TaskStatus[] keyChangeTaskStatuses = Array.Empty<TaskStatus>();

            if (resourcesWithUpdatableKeys.Any())
            {
                var keyChangeDependenciesByResourcePath = GetKeyChangeDependencies(dependencyKeysByResourceKey, resourcesWithUpdatableKeys);

                // Probe for key changes support (using first by name sorted alphabetically for deterministic behavior)
                string probeResourceKey = keyChangeDependenciesByResourcePath.Keys.MinBy(x => x);

                if (probeResourceKey == null)
                {
                    _logger.Information($"None of the API resources configured to allow key changes were found in the resources to be processed (based on the resource dependency metadata).");

                    return Array.Empty<TaskStatus>();
                }

                var supportsKeyChanges = await _sourceCapabilities.SupportsKeyChangesAsync(probeResourceKey);
                
                if (supportsKeyChanges)
                {
                    _logger.Debug($"Source supports key changes. Initiating key changes processing.");

                    using var processingSemaphore = new SemaphoreSlim(
                        options.MaxDegreeOfParallelismForResourceProcessing,
                        options.MaxDegreeOfParallelismForResourceProcessing);

                    var processingContext = new ProcessingContext(
                        changeWindow,
                        keyChangeDependenciesByResourcePath,
                        errorPublishingBlock,
                        processingSemaphore,
                        options,
                        authorizationFailureHandling,
                        resourcesWithUpdatableKeys,
                        null,
                        EdFiApiConstants.KeyChangesPathSuffix
                    );

                    var initiator = _publishingStageInitiatorByStage[PublishingStage.KeyChanges];

                    var streamingPagesOfKeyChangesByResourcePath = initiator.Start(processingContext, cancellationToken);

                    // Wait for everything to finish
                    keyChangeTaskStatuses = WaitForResourceStreamingToComplete(
                        "key changes",
                        streamingPagesOfKeyChangesByResourcePath,
                        processingSemaphore,
                        options);
                }
                else
                {
                    _logger.Warning($"Source indicated a lack of support for key changes. Key change processing cannot be performed.");
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


        private TaskStatus[] WaitForResourceStreamingToComplete(
            string activityDescription,
            IDictionary<string, StreamingPagesItem> streamingPagesByResourcePath,
            SemaphoreSlim processingSemaphore,
            Options options)
        {
            var completedStreamingPagesByResourcePath = new Dictionary<string, TaskStatus>(StringComparer.OrdinalIgnoreCase);

            _logger.Information($"Waiting for {streamingPagesByResourcePath.Count} {activityDescription} streaming sources to complete...");

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
                    
                    if (_logger.IsEnabled(LogEventLevel.Debug))
                    {
                        _logger.Debug(logMessage);
                    }
                    else 
                    {
                        _logger.Information(logMessage);
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
                    
                    completedStreamingPagesByResourcePath.Add(
                        resourcePaths[completedIndex],
                        streamingPagesByResourcePath[resourcePaths[completedIndex]].CompletionBlock.Completion.Status);

                    streamingPagesItem.CompletionBlock = null;
                    
                    streamingPagesByResourcePath.Remove(resourcePaths[completedIndex]);

                    if (_logger.IsEnabled(LogEventLevel.Debug))
                    {
                        _logger.Debug($"Streaming of '{resourcePaths[completedIndex]}' completed. Releasing a processing slot ({processingSemaphore.CurrentCount} slots currently available)...");
                    }

                    _logger.Information($"Streaming of '{resourcePaths[completedIndex]}' completed. {streamingPagesByResourcePath.Count} resource(s) remaining. {processingSemaphore.CurrentCount + 1} processing slot(s) soon to be available.");

                    try
                    {
                        processingSemaphore.Release();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Attempt to release the semaphore resulted in an exception: {ex}");
                    }
                }
            }

            return completedStreamingPagesByResourcePath
                .Select(kvp => kvp.Value)
                .ToArray();
        }
    }

    public record ProcessingContext(
        ChangeWindow ChangeWindow,
        IDictionary<string, string[]> DependencyKeysByResourceKey,
        ITargetBlock<ErrorItemMessage> PublishErrorsIngestionBlock,
        SemaphoreSlim Semaphore,
        Options Options,
        AuthorizationFailureHandling[] AuthorizationFailureHandling,
        string[] ResourcesWithUpdatableKeys,
        Func<string> JavaScriptModuleFactory,
        string ResourceUrlPathSuffix)
    {
        public override string ToString()
        {
            return
                $"{{ ChangeWindow = {ChangeWindow}, DependencyKeysByResourceKey = {DependencyKeysByResourceKey}, PublishErrorsIngestionBlock = {PublishErrorsIngestionBlock}, Semaphore = {Semaphore}, ResourceUrlPathSuffix = {ResourceUrlPathSuffix}, Options = {Options}, AuthorizationFailureHandling = {AuthorizationFailureHandling}, ResourcesWithUpdatableKeys = {ResourcesWithUpdatableKeys} }}";
        }
    }
}
