using System;
using Newtonsoft.Json;

namespace EdFi.Tools.ApiPublisher.Tests.Serialization
{
    public class GuidConverter : JsonConverter
    {
        public override bool CanRead
        {
            get { return false; }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(((Guid) value).ToString("N"));
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            throw new NotImplementedException("Method not implemented because 'CanRead' implementation will cause this to never be called.");
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(Guid);
        }
    }
}