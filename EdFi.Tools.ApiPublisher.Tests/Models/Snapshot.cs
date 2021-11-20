using System;
using EdFi.Tools.ApiPublisher.Tests.Serialization;
using Newtonsoft.Json;

namespace EdFi.Tools.ApiPublisher.Tests.Models
{
    /// <summary>
    /// A class which represents the changes.Snapshot table of the Snapshot aggregate in the ODS Database.
    /// </summary>
    public class Snapshot
    {
        /// <summary>
        /// The unique identifier for the Snapshot resource.
        /// </summary>
        [JsonConverter(typeof(GuidConverter))]
        [JsonProperty("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// The unique identifier of the snapshot.
        /// </summary>
        // NOT in a reference, NOT a lookup column 
        [JsonProperty("snapshotIdentifier")]
        public string SnapshotIdentifier { get; set; }

        /// <summary>
        /// The date and time that the snapshot was initiated.
        /// </summary>
        // NOT in a reference, NOT a lookup column 
        [JsonProperty("snapshotDateTime")]
        public DateTime SnapshotDateTime { get; set; }
    }
}