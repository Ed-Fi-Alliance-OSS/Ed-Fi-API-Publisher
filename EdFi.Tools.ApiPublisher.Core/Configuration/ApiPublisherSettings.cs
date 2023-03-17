using System;
using System.Threading;
using Serilog;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ApiPublisherSettings
    {
        public Options Options { get; set; }

        public AuthorizationFailureHandling[] AuthorizationFailureHandling { get; set; }
        
        public string[] ResourcesWithUpdatableKeys { get; set; }
    }

    public class AuthorizationFailureHandling
    {
        public string Path { get; set; }
        public string[] UpdatePrerequisitePaths { get; set; }
    }

    public class Options
    {
        private readonly ILogger _logger = Log.Logger;
        
        public int BearerTokenRefreshMinutes { get; set; } = 12;
        
        public int RetryStartingDelayMilliseconds { get; set; } = 250;
        
        public int MaxRetryAttempts { get; set; } = 5;

        public int MaxDegreeOfParallelismForResourceProcessing { get; set; } = 10;
        
        private int _maxDegreeOfParallelismForPostResourceItem = 20;

        public int MaxDegreeOfParallelismForPostResourceItem
        {
            get => _maxDegreeOfParallelismForPostResourceItem;
            set
            {
                if (value <= 0)
                {
                    _logger.Warning($"Attempted max parallelism of '{value}' for posting resources is invalid. Setting has been adjusted to '1'.");
                    _maxDegreeOfParallelismForPostResourceItem = 1;

                    return;
                }
                
                // Limit setting to the number of threads available
                ThreadPool.GetMaxThreads(out int workerThreadCount, out int completionPortThreadCount);

                // Cap the maximum parallelization at a reasonable level of 200
                // (GetMaxThreads could return a number as high as 32,767 depending on the environment)
                int practicalMaxParallelization = Math.Min(200, workerThreadCount);

                _maxDegreeOfParallelismForPostResourceItem = Math.Min(value, practicalMaxParallelization);
                
                if (value > _maxDegreeOfParallelismForPostResourceItem)
                {
                    _logger.Warning($"Attempted max parallelism of '{value}' for posting resources is too large. Setting has been adjusted to '{_maxDegreeOfParallelismForPostResourceItem}'.");
                }
            }
        }

        public int MaxDegreeOfParallelismForStreamResourcePages { get; set; } = 5;

        public int StreamingPagesWaitDurationSeconds { get; set; } = 10;

        public int StreamingPageSize { get; set; } = 75;

        public bool IncludeDescriptors { get; set; } = false;

        public bool WhatIf { get; set; } = false;

        public int ErrorPublishingBatchSize { get; set; } = 25;

        public bool IgnoreSSLErrors { get; set; } = false;

        public string RemediationsScriptFile { get; set; }

        public bool UseSourceDependencyMetadata { get; set; }
    }
}