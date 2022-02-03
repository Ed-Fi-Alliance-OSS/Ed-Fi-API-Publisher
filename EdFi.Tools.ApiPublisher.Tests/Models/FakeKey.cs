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