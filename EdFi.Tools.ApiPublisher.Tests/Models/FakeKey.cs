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
    }
}