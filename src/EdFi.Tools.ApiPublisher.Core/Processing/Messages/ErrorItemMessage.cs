// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Newtonsoft.Json.Linq;
using System;
using System.Net;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    public class ErrorItemMessage
    {
        public ErrorItemMessage()
        {
            DateTime = DateTime.UtcNow;
        }

        public DateTime DateTime { get; }

        public string Method { get; set; }

        public string ResourceUrl { get; set; }

#nullable enable
        public string? Id { get; set; }

        /// <summary>
        /// The JSON body of the failed request. Producers should assign a compact <see cref="JRaw" />
        /// (serialized once at error creation) rather than a live <see cref="JObject" /> so that errors
        /// queued for publishing do not retain full parsed token graphs in memory (see APIPUB-112).
        /// </summary>
        //[JsonIgnore]
        public JToken? Body { get; set; }

        public HttpStatusCode? ResponseStatus { get; set; }

        public string ResponseContent { get; set; }

        public Exception Exception { get; set; }
    }
}
