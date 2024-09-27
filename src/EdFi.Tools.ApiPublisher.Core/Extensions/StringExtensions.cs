// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Extensions
{
    public static class StringExtensions
    {
        public static string EnsureSuffixApplied(this string text, string suffix)
        {
            if (string.IsNullOrEmpty(text))
            {
                return suffix;
            }

            if (text.EndsWith(suffix))
            {
                return text;
            }

            return text + suffix;
        }

        public static bool TryTrimSuffix(this string text, string suffix, out string trimmedText)
        {
            trimmedText = null;

            if (text == null)
            {
                return false;
            }

            int pos = text.LastIndexOf(suffix);

            if (pos < 0)
            {
                return false;
            }

            if (text.Length - pos == suffix.Length)
            {
                trimmedText = text.Substring(0, pos);
                return true;
            }

            return false;
        }

        public static string TrimSuffix(this string text, string suffix)
        {
            string trimmedText;

            if (TryTrimSuffix(text, suffix, out trimmedText))
            {
                return trimmedText;
            }

            return text;
        }

        /// <summary>
        /// Returns a string that is converted to camel casing, detecting and handling acronyms as prefixes and suffixes.
        /// </summary>
        /// <param name="text">The text to be processed.</param>
        /// <returns>A string that has the first character converted to lower-case.</returns>
        public static string ToCamelCase(this string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            if (text.Length == 1)
            {
                return text.ToLower();
            }

            int leadingUpperCharsLength = text.TakeWhile(char.IsUpper).Count();

            int prefixLength = leadingUpperCharsLength - 1;

            if (text.Length == leadingUpperCharsLength

                // Handles the case of an acronym with a trailing "s" (e.g. "URIs" -> "uris" not "urIs")
                || text.Length == leadingUpperCharsLength + 1 && text.EndsWith("s"))
            {
                // Convert entire name to lower case
                return text.ToLower();
            }

            if (prefixLength > 0)
            {
                // Apply lower casing to leading acronym
                return text.Substring(0, prefixLength)
                        .ToLower()
                    + text.Substring(prefixLength);
            }

            // Apply simple camel casing
            return char.ToLower(text[0]) + text.Substring(1);
        }

        public static bool EqualsIgnoreCase(this string text, string compareText) => text == null ? compareText == null : text.Equals(compareText, StringComparison.InvariantCultureIgnoreCase);

        public static bool StartsWithIgnoreCase(this string text, string compareText) => text == null ? compareText == null : text.StartsWith(compareText, StringComparison.InvariantCultureIgnoreCase);

        public static bool EndsWithIgnoreCase(this string text, string compareText) => text == null ? compareText == null : text.EndsWith(compareText, StringComparison.InvariantCultureIgnoreCase);

        public static bool ContainsIgnoreCase(this string text, string compareText) => text != null && compareText != null && text.IndexOf(compareText, StringComparison.InvariantCultureIgnoreCase) >= 0;
    }
}
