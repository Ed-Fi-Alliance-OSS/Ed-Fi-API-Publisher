// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using System;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    /// <summary>
    /// The process exit codes of the API Publisher. An unattended caller can act on the outcome of a run
    /// without parsing the log: every code other than <see cref="Success" /> means data was not fully
    /// published, and the specific code says what to do about it (see APIPUB-120).
    /// </summary>
    public static class PublisherExitCode
    {
        /// <summary>
        /// Everything the run set out to publish was published, or the documents that failed were within
        /// the tolerance configured through <see cref="Options.ToleratedItemErrorCount" />.
        /// </summary>
        public const int Success = 0;

        /// <summary>
        /// Every resource ran to completion, but documents were rejected by the target beyond the configured
        /// tolerance. The run summary reports which resources lost documents.
        /// </summary>
        public const int CompletedWithItemErrors = 1;

        /// <summary>
        /// The run did not complete, so what was published is unknown. Re-running is the expected response.
        /// </summary>
        public const int ProcessingIncomplete = 2;

        /// <summary>
        /// The publisher could not authenticate against the source or target API.
        /// </summary>
        public const int AuthenticationFailure = 3;

        /// <summary>
        /// The configuration or the supplied options are invalid, so no publishing was attempted.
        /// </summary>
        public const int InvalidConfiguration = 4;

        /// <summary>
        /// Maps a failure to the exit code that describes it.
        /// </summary>
        /// <remarks>
        /// An authentication failure is recognized by the caller instead of here, because the exception that
        /// represents it belongs to the API connection plugin rather than to the core pipeline.
        /// </remarks>
        public static int ForFailure(Exception exception)
            => exception switch
            {
                InvalidConfigurationException => InvalidConfiguration,
                PublishingFailedException { Reason: PublishingFailureReason.ItemErrors } => CompletedWithItemErrors,
                _ => ProcessingIncomplete,
            };
    }
}
