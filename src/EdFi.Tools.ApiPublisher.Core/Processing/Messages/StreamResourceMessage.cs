// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    public class StreamResourceMessage
    {
        // ----------------------------
        // Resource-specific context
        // ----------------------------
        public string ResourceUrl { get; set; }
        public Task[] Dependencies { get; set; }
        public string[] DependencyPaths { get; set; }
        public bool ShouldSkip { get; set; }

        // Source Ed-Fi ODS API processing context (resource-specific)
        // Indicates an authorization-retry ("#Retry") pipeline exists for this resource, which re-publishes
        // the entire resource after its update prerequisites complete (see APIPUB-133), so a 403 on an
        // individual item can be safely skipped without publishing an error.
        public bool HasAuthorizationRetryPipeline { get; set; }

        // -------------------------------------------------
        // Source Ed-Fi ODS API processing context (shared)
        // -------------------------------------------------
        // public EdFiApiClient EdFiApiClient { get; set; }

        // NOTE: This is potentially not Ed-Fi ODs API-specific, but likely so
        public int PageSize { get; set; }

        // ----------------------------
        // Global processing context
        // ----------------------------
        public CancellationTokenSource CancellationSource { get; set; }
        public SemaphoreSlim ProcessingSemaphore { get; set; }
        public ChangeWindow ChangeWindow { get; set; }
    }
}
