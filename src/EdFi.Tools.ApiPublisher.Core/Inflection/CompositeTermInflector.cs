// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EdFi.Common.Inflection
{
    public static class CompositeTermInflector
    {
        private static readonly HashSet<string> _ignoredSuffixes = new HashSet<string>();

        private static readonly ConcurrentDictionary<string, string> _pluralizedByTerm
            = new ConcurrentDictionary<string, string>();

        private static readonly ConcurrentDictionary<string, string> _singularizedByTerm
            = new ConcurrentDictionary<string, string>();

        static CompositeTermInflector()
        {
            AddIgnoredSuffix("Offered");
        }

        public static void AddIgnoredSuffix(string suffix)
        {
            _ignoredSuffixes.Add(suffix.ToLower());
        }

        public static string MakePlural(string compositeTerm)
        {
            return _pluralizedByTerm.GetOrAdd(
                compositeTerm,
                t =>
                {
                    List<string> result = new List<string>();

                    // Split the composite term based on mixed-case conventions
                    var matches = Regex.Matches(t, "((?:^[a-z]+|[A-Z]+)(?:[a-z0-9]+)?)");

                    bool isCompositeTermPluralized = false;

                    for (int i = matches.Count - 1; i >= 0; i--)
                    {
                        string term = matches[i]
                           .Value;

                        if (isCompositeTermPluralized || _ignoredSuffixes.Contains(term.ToLower()))
                        {
                            result.Insert(0, term);
                            continue;
                        }

                        // Pluralize the current term
                        string pluralizedTerm = Inflector.MakePlural(term);
                        result.Insert(0, pluralizedTerm);
                        isCompositeTermPluralized = true;
                    }

                    return string.Join(string.Empty, result);
                });
        }

        public static string MakeSingular(string compositeTerm)
        {
            return _singularizedByTerm.GetOrAdd(
                compositeTerm,
                t =>
                {
                    List<string> result = new List<string>();

                    // Split the composite term based on mixed-case conventions
                    var matches = Regex.Matches(t, "((?:^[a-z]+|[A-Z]+)(?:[a-z0-9]+)?)");

                    bool isCompositeTermSingularized = false;

                    for (int i = matches.Count - 1; i >= 0; i--)
                    {
                        string term = matches[i]
                           .Value;

                        if (isCompositeTermSingularized || _ignoredSuffixes.Contains(term.ToLower()))
                        {
                            result.Insert(0, term);
                            continue;
                        }

                        // Pluralize the current term
                        string singularizedTerm = Inflector.MakeSingular(term);
                        result.Insert(0, singularizedTerm);
                        isCompositeTermSingularized = true;
                    }

                    return string.Join(string.Empty, result);
                });
        }
    }
}
