// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Text.RegularExpressions;

namespace EdFi.Tools.ApiPublisher.Tests.Serialization
{
	public class Iso8601UtcDateOnlyConverter : IsoDateTimeConverter
    {
        // All valid US English time formats will contain either a time separator ':' or an AM/PM designator
        private readonly Regex _timePortionRegex = new Regex(":|am|pm", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const string OutputDateFormat = "yyyy-MM-dd";

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                                        JsonSerializer serializer)
        {
            if (reader.DateParseHandling != DateParseHandling.None)
            {
                throw new ApplicationException(
                    "This converter is only valid when used with DateParseHandling.None. " +
                    "This is due to the built in functionality of Json.Net forcing date parsing by default on " +
                    "any strings it finds that happen to match ISO8601 format. " +
                    "See https://github.com/JamesNK/Newtonsoft.Json/issues/862 for additional information.");
            }

            object result;

            string value = reader.Value.ToString();

            if (_timePortionRegex.IsMatch(value))
            {
                throw new FormatException("String was not recognized as a valid date.");
            }

            try
            {
                result = base.ReadJson(reader, objectType, existingValue, serializer);
            }
            catch (FormatException ex)
            {
                // Convert the message generated by a parsing error to indicate the value isn't a valid "date" (rather than "DateTime").
                throw new FormatException(ex.Message.Replace("DateTime", "date"), ex);
            }

            return result;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            string originalDateTimeFormat = DateTimeFormat;

            // Only set the format for output, then set it back to the original value
            // This ensures output is a standard format but that the input parse isn't affected
            // without reimplementing the base method
            DateTimeFormat = OutputDateFormat;
            base.WriteJson(writer, value, serializer);
            DateTimeFormat = originalDateTimeFormat;
        }
    }
}
