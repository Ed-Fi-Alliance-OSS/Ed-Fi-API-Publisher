// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using Newtonsoft.Json;

namespace EdFi.Tools.ApiPublisher.Tests.Models
{
    public class FakeKey
    {
        [JsonProperty("name")]
        public string Name { get; set; }
        
        [JsonProperty("birthDate")]
        public DateTime BirthDate { get; set; }
        
        [JsonProperty("retirementAge")]
        public int RetirementAge { get; set; }

        [JsonProperty("link")]
        public Link Link { get; set; }
    }

    public class Link
    {
        [JsonProperty("rel")]
        public string Rel { get; set; }
        
        [JsonProperty("href")]
        public string Href { get; set; }
    }
}
