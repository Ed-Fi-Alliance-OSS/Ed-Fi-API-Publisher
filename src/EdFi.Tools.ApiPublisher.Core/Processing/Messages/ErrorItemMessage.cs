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
        /// Locates the source page the failed item was read from (paging metadata only, e.g. offset, limit and
        /// change window -- see <see cref="StreamResourcePageMessage{TProcessDataMessage}.DescribeSourcePage" />),
        /// when known. Together with <see cref="SourceItemIndex" /> this lets an operator find the offending
        /// source document without re-running at Debug level.
        /// </summary>
        public string? SourcePage { get; set; }

        /// <summary>
        /// The zero-based position of the failed item within its source page's JSON array, when known.
        /// </summary>
        public int? SourceItemIndex { get; set; }

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
