// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Serilog;
using System;
using System.Threading;

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
        /// <summary>
        /// Multiplier applied to <see cref="MaxDegreeOfParallelismForPostResourceItem" /> when deriving the
        /// automatic processing-block capacity: enough buffered items to keep every POST worker busy through
        /// several rounds of refills without reintroducing meaningful memory retention (see APIPUB-112).
        /// </summary>
        public const int AutoCapacityPostParallelismMultiplier = 4;

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

        /// <summary>
        /// Caps the number of items that each resource-processing Dataflow block will buffer so that a slow
        /// target exerts backpressure on source page streaming, rather than buffering source items in memory
        /// without limit (see APIPUB-112). A value of 0 (the default) derives the capacity automatically from
        /// <see cref="StreamingPageSize" /> and <see cref="MaxDegreeOfParallelismForPostResourceItem" />;
        /// -1 disables the bound entirely (restoring the pre-APIPUB-112 behavior). Values below -1 are invalid:
        /// they are rejected by CLI options validation, and the resolved capacity properties throw if one is
        /// ever used directly.
        /// </summary>
        public int ProcessingBlockBoundedCapacity { get; set; }

        /// <summary>
        /// Gets the effective bounded capacity to apply to resource-processing Dataflow blocks. Returns -1
        /// (equivalent to <c>DataflowBlockOptions.Unbounded</c>) when bounding has been explicitly disabled.
        /// An explicit capacity is never allowed below <see cref="MaxDegreeOfParallelismForPostResourceItem" />
        /// so that item-level parallelism cannot be starved by the bound.
        /// </summary>
        public int ResolvedProcessingBlockBoundedCapacity
            => ProcessingBlockBoundedCapacity switch
            {
                < -1 => throw new InvalidOperationException(
                    $"Processing block bounded capacity of '{ProcessingBlockBoundedCapacity}' is invalid. Valid values are -1 (unbounded), 0 (automatic), or a positive capacity."),
                -1 => -1,
                0 => Math.Max(StreamingPageSize, AutoCapacityPostParallelismMultiplier * MaxDegreeOfParallelismForPostResourceItem),
                _ => Math.Max(ProcessingBlockBoundedCapacity, MaxDegreeOfParallelismForPostResourceItem),
            };

        /// <summary>
        /// Gets the effective bounded capacity for the block that fetches pages of source items. This capacity
        /// is denominated in page messages rather than items: a TransformManyBlock's bound only gates the
        /// acceptance of new inputs, and every accepted page message still expands into a full page of items,
        /// so the item-denominated <see cref="ResolvedProcessingBlockBoundedCapacity" /> would allow that many
        /// whole pages of items to materialize. Returns -1 when bounding is disabled.
        /// </summary>
        public int ResolvedStreamResourcePagesBlockBoundedCapacity
            => ResolvedProcessingBlockBoundedCapacity == -1
                ? -1
                : Math.Max(
                    2 * MaxDegreeOfParallelismForStreamResourcePages,
                    ResolvedProcessingBlockBoundedCapacity / Math.Max(1, StreamingPageSize));

        /// <summary>
        /// Gets the effective bounded capacity for the error publishing ingestion block. Errors awaiting
        /// publication would otherwise queue without limit when they are produced faster than they can be
        /// published (e.g. during a sustained authorization-failure storm -- see APIPUB-112). The capacity
        /// is denominated in error messages and is kept at twice <see cref="ErrorPublishingBatchSize" /> so
        /// batches can always form. Returns -1 when bounding is disabled via
        /// <see cref="ProcessingBlockBoundedCapacity" />.
        /// </summary>
        public int ResolvedErrorPublishingBoundedCapacity
            => ResolvedProcessingBlockBoundedCapacity == -1
                ? -1
                : 2 * Math.Max(1, ErrorPublishingBatchSize);

        public int StreamingPagesWaitDurationSeconds { get; set; } = 10;

        public int StreamingPageSize { get; set; } = 75;

        public bool IncludeDescriptors { get; set; } = false;

        public bool WhatIf { get; set; } = false;

        public int ErrorPublishingBatchSize { get; set; } = 25;

        public bool IgnoreSSLErrors { get; set; } = false;

        public string RemediationsScriptFile { get; set; }

        public bool UseSourceDependencyMetadata { get; set; }

        public bool UseChangeVersionPaging { get; set; }

        public int ChangeVersionPagingWindowSize { get; set; }

        public bool EnableRateLimit { get; set; } = false;

        public int RateLimitNumberExecutions { get; set; } = 30;

        public double RateLimitTimeSeconds { get; set; } = 1;

        public int RateLimitMaxRetries { get; set; } = 5;

        public bool UseReversePaging { get; set; } = false;

        public string LastChangeVersionProcessedNamespace { get; set; }

        public bool ProcessDeletesAndKeyChangesOnFullPublish { get; set; } = false;
    }
}
