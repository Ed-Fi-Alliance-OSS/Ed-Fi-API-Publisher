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
        /// The JSON body of the failed request as a raw JSON string, serialized once at error creation.
        /// Typed as <see cref="JRaw" /> (rather than a parsed token type) so that errors queued for
        /// publishing retain a single string instead of a full parsed token graph (see APIPUB-112), and so
        /// that no implicit conversion can silently assign non-JSON content.
        /// </summary>
        public JRaw? Body { get; set; }

        public HttpStatusCode? ResponseStatus { get; set; }

        public string ResponseContent { get; set; }

        public Exception Exception { get; set; }
    }
}
