// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages
{
    public class PostItemMessage
    {
        public string ResourceUrl { get; set; }

        public JObject Item { get; set; }

        /// <summary>
        /// Indicates an authorization-retry ("#Retry") pipeline exists that will re-publish the entire resource
        /// after its update prerequisites complete, so a 403 response for this item can be skipped without
        /// publishing an error (see APIPUB-133).
        /// </summary>
        public bool HasAuthorizationRetryPipeline { get; set; }

        /// <summary>
        /// Cancellation token from the resource's processing cancellation source, used to abandon in-flight
        /// requests when processing of the resource has been cancelled.
        /// </summary>
        public CancellationToken CancellationToken { get; set; }
    }
}
