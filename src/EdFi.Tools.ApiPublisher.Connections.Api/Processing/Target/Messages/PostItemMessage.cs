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

        /// <summary>
        /// The source item's "id", stamped the first time processing reads a valid id from
        /// <see cref="Item"/> so that it survives even after <see cref="Item"/> is later released (e.g. by
        /// a deferred authorization-failure retry re-entering processing for this same message object).
        /// </summary>
        public string Id { get; set; }

        public JObject Item { get; set; }

        public Action<object> PostAuthorizationFailureRetry { get; set; }
    }
}
