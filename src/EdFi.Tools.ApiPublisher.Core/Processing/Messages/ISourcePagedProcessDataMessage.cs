// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    /// <summary>
    /// Implemented by a processing message that knows which source page its document came from, so that a
    /// run can record the pages it is safely past and resume after them (see APIPUB-142). A message that
    /// does not implement it, or that carries a null reference, is not checkpointed and is read again in
    /// full by a resumed run.
    /// </summary>
    public interface ISourcePagedProcessDataMessage
    {
        /// <summary>
        /// Gets the cursor-paged source page this document was read from, or null when it was not read with
        /// cursor paging.
        /// </summary>
        SourcePageReference SourcePageReference { get; }
    }
}
