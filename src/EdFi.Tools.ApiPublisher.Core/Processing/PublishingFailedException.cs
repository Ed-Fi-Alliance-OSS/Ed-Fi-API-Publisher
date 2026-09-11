// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    /// <summary>
    /// Thrown when a publishing run must be reported as a failure. Carries the counts behind the decision and
    /// a <see cref="PublishingFailureReason" /> so that the caller can report a specific outcome instead of
    /// inferring one from a message (see APIPUB-120).
    /// </summary>
    public class PublishingFailedException : Exception
    {
        public PublishingFailedException(
            string message,
            PublishingFailureReason reason,
            long itemErrorCount = 0,
            int incompleteResourceCount = 0,
            Exception innerException = null)
            : base(message, innerException)
        {
            Reason = reason;
            ItemErrorCount = itemErrorCount;
            IncompleteResourceCount = incompleteResourceCount;
        }

        public PublishingFailureReason Reason { get; }

        /// <summary>
        /// The number of documents reported as errors during the run.
        /// </summary>
        public long ItemErrorCount { get; }

        /// <summary>
        /// The number of resources whose processing did not run to completion.
        /// </summary>
        public int IncompleteResourceCount { get; }
    }
}
