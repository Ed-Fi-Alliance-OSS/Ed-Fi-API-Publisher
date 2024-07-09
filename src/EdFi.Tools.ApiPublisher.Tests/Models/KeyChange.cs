// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Tests.Extensions;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace EdFi.Tools.ApiPublisher.Tests.Models
{
	public class KeyChange<TKey>
    {
        public string Id { get; set; }

        public long ChangeVersion { get; set; }

        [JsonIgnore]
        public TKey OldKeyValuesObject { get; set; }

        [JsonIgnore]
        public TKey NewKeyValuesObject { get; set; }

        private IDictionary<string, object> _oldValues;

        public IDictionary<string, object> OldKeyValues
        {
            get => _oldValues ?? OldKeyValuesObject.ToDictionary();
            set => _oldValues = value;
        }

        private IDictionary<string, object> _newValues;

        public IDictionary<string, object> NewKeyValues
        {
            get => _newValues ?? NewKeyValuesObject.ToDictionary();
            set => _newValues = value;
        }
    }
}
