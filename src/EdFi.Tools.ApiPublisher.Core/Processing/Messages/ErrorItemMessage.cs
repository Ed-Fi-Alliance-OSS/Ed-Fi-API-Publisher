// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Net;
using Newtonsoft.Json.Linq;

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
        
        public string? Id { get; set; }

        //[JsonIgnore]
        public JObject? Body { get; set; }

        public HttpStatusCode? ResponseStatus { get; set; }
        
        public string ResponseContent { get; set; }
        
        public Exception Exception { get; set; }
    }
}
