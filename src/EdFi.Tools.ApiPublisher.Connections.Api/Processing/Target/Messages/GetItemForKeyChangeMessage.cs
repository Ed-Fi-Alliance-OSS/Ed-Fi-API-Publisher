// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages
{
    public class GetItemForKeyChangeMessage
    {
        /// <summary>
        /// Gets or sets the relative URL for the resource whose key is to be changed.
        /// </summary>
        public string ResourceUrl { get; set; }
        
        /// <summary>
        /// Gets or sets the existing natural key values for the resource on the target whose key is to be changed.
        /// </summary>
        public JToken ExistingKeyValues { get; set; }
        
        /// <summary>
        /// Gets or sets the new natural key values for the resource on the target whose key is to be changed.
        /// </summary>
        public JToken NewKeyValues { get; set; }
        
        /// <summary>
        /// Gets or sets the source API's resource identifier for the resource whose key was changed.
        /// </summary>
        /// <remarks>This is captured for informational purposes only.</remarks>
        public string SourceId { get; set; }

        /// <summary>
        /// Gets or sets the cancellation token indicating whether key change processing should proceed.
        /// </summary>
        public CancellationToken CancellationToken { get; set; }
    }
}
