// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    /// <summary>
    /// The outcome of streaming a single resource through a publishing stage. The exception behind a faulted
    /// task is retained so that a failed run can name its cause at the top level, rather than reducing the
    /// failure to a <see cref="TaskStatus" /> and discarding the reason (see APIPUB-120).
    /// </summary>
    /// <param name="ResourcePath">The resource path (including the stage's path suffix, when it has one).</param>
    /// <param name="Status">The final status of the resource's completion block.</param>
    /// <param name="Exception">The exception behind a faulted completion block, or <c>null</c>.</param>
    public record ResourceStreamingOutcome(string ResourcePath, TaskStatus Status, AggregateException Exception)
    {
        public bool RanToCompletion => Status == TaskStatus.RanToCompletion;
    }
}
