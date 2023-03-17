// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages
{
    public class ChangeKeyMessage
    {
        /// <summary>
        /// Gets or sets the relative URL for the resource whose key is to be changed.
        /// </summary>
        public string ResourceUrl { get; set; }

        /// <summary>
        /// Gets or sets the target API's resource identifier for the resource whose key is to be changed.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Gets or sets the target API's existing resource body with the new key values applied -- to be PUT against the target API.
        /// </summary>
        public string Body { get; set; }
        
        /// <summary>
        /// Gets or sets the source API's resource identifier for the resource whose key was changed (primarily for correlating activity in log messages).
        /// </summary>
        public string SourceId { get; set; }
    }
}
