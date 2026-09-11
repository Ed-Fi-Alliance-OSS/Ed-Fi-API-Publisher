// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    /// <summary>
    /// Implemented by a processing message that carries more than one source document, so that the run summary
    /// counts documents rather than messages. A message that does not implement it represents one document.
    /// </summary>
    public interface IItemCountedProcessDataMessage
    {
        /// <summary>
        /// Gets the number of source documents this message carries.
        /// </summary>
        int ItemCount { get; }
    }
}
