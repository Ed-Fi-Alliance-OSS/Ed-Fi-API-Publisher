using System;
using Newtonsoft.Json;

namespace EdFi.Tools.ApiPublisher.Tests.Models
{
    public class GenericResource<TKey>
    {
        [JsonProperty("id")]
        public string Id { get; set; }
        
        [JsonProperty("someReference")]
        public TKey SomeReference { get; set; }

        [JsonProperty("vehicleYear")]
        public int VehicleYear { get; set; }
        
        [JsonProperty("vehicleManufacturer")]
        public string VehicleManufacturer { get; set; }
    }
}