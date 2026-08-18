// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;

namespace EdFi.Tools.ApiPublisher.Core.Helpers
{
    public static class JsonHelpers
    {
        /// <summary>
        /// Load settings that skip Newtonsoft's default line-info tracking. Parsed documents that are held in
        /// processing queues otherwise retain several <c>LineInfoAnnotation</c> objects per token subtree, which
        /// are only ever used for parse-error messages (see APIPUB-112). A fresh instance is returned on each
        /// access because <see cref="JsonLoadSettings" /> is mutable -- a shared instance could be modified by
        /// one caller and silently change parsing behavior everywhere.
        /// </summary>
        public static JsonLoadSettings NoLineInfoLoadSettings
            => new JsonLoadSettings { LineInfoHandling = LineInfoHandling.Ignore };

        /// <summary>
        /// Validates the supplied JSON with a streaming reader (no token graph is materialized) and returns
        /// it wrapped as a <see cref="JRaw" /> for compact retention on an error message, or null if the
        /// text is empty or not valid JSON.
        /// </summary>
        public static JRaw ToValidatedJsonRawOrDefault(string json)
        {
            JRaw body = null;

            try
            {
                using var reader = new JsonTextReader(new StringReader(json));

                bool hasContent = false;

                while (reader.Read())
                {
                    hasContent = true;
                }

                if (hasContent)
                {
                    body = new JRaw(json);
                }
            }
            catch
            {
                // Not valid JSON -- omit the body rather than corrupt the serialized error output
            }

            return body;
        }

        /// <summary>
        /// Counts the elements of a top-level JSON array without materializing a <see cref="JToken" /> graph.
        /// </summary>
        /// <param name="json">The JSON text, expected to be a top-level array.</param>
        /// <returns>The number of elements in the top-level array.</returns>
        /// <exception cref="JsonReaderException">Thrown if the text is not valid JSON or is not a top-level array.</exception>
        public static int CountTopLevelArrayItems(string json)
        {
            using var reader = new JsonTextReader(new StringReader(json));

            if (!reader.Read() || reader.TokenType != JsonToken.StartArray)
            {
                throw new JsonReaderException("Expected a top-level JSON array.");
            }

            int count = 0;

            while (reader.Read())
            {
                if (reader.Depth == 1
                    && reader.TokenType != JsonToken.EndArray
                    && reader.TokenType != JsonToken.EndObject
                    && reader.TokenType != JsonToken.Comment)
                {
                    count++;
                }

                // Skip the interior of any non-scalar element in a single step (constructors are non-standard
                // JSON that JsonTextReader accepts; without the skip, EndConstructor would count as an element)
                if (reader.TokenType == JsonToken.StartObject
                    || reader.TokenType == JsonToken.StartArray
                    || reader.TokenType == JsonToken.StartConstructor)
                {
                    reader.Skip();
                }
            }

            return count;
        }
    }
}
