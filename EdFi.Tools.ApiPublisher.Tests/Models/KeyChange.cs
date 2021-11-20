using System;
using System.Collections.Generic;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using Newtonsoft.Json;

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