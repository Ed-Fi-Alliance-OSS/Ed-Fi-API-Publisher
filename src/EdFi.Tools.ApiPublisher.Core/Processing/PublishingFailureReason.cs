// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    /// <summary>
    /// Why a publishing run failed. Callers (the CLI included) map this to an exit code, so that an
    /// unattended run can tell a completed-but-lossy publish apart from one that broke (see APIPUB-120).
    /// </summary>
    public enum PublishingFailureReason
    {
        /// <summary>
        /// Every resource ran to completion, but documents were rejected by the target and the resulting
        /// error count exceeded the configured tolerance. The remaining documents were published.
        /// </summary>
        ItemErrors,

        /// <summary>
        /// The run did not complete: one or more resources faulted or were cancelled, the errors themselves
        /// could not be published, or a finalization activity failed. What was published is unknown.
        /// </summary>
        IncompleteProcessing
    }
}
