// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Tests.Serialization;
using Newtonsoft.Json;
using System;

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
