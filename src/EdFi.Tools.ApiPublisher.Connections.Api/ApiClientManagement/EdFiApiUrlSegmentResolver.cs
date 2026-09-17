// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing;
using Newtonsoft.Json.Linq;
using Serilog;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Describes one of the path segments the publisher prefixes onto its requests: the name the Discovery
    /// document publishes it under, the value to assume when the API does not declare it, and the connection
    /// setting an operator uses to state it outright.
    /// </summary>
    public sealed record ApiUrlSegmentDefinition(
        string DiscoveryUrlName,
        string ConventionalSegment,
        string ConfigurationKeyName
    );

    /// <summary>
    /// Resolves the path segments the publisher prefixes onto its requests, preferring what the connection
    /// states outright, then what the API declares in its Discovery document, and falling back to the
    /// conventional ODS/API value when neither is available.
    /// </summary>
    /// <remarks>
    /// A segment is held relative to the connection's own URL rather than as an absolute path. An API served
    /// under a path prefix, which is how a multi-tenant DMS is addressed, repeats that prefix in every URL it
    /// declares; appending the absolute path of one of those to a connection URL that already carries the
    /// prefix would state it twice.
    /// </remarks>
    public class EdFiApiUrlSegmentResolver
    {
        /// <summary>
        /// Gets the advice shared by everything that refuses a path carrying a route placeholder.
        /// </summary>
        public const string RouteQualifierGuidance =
            "An API that qualifies its routes by tenant or school year resolves them only for an address that names one, so the URL for this connection has to include that prefix (for example 'https://server/tenant/') rather than the server root.";

        public static readonly ApiUrlSegmentDefinition DataManagement =
            new(
                DiscoveryUrlName: "dataManagementApi",
                ConventionalSegment: EdFiApiConstants.DataManagementApiSegment,
                ConfigurationKeyName: "DataManagementUrlSegment"
            );

        public static readonly ApiUrlSegmentDefinition ChangeQueries =
            new(
                DiscoveryUrlName: "changeQueries",
                ConventionalSegment: EdFiApiConstants.ChangeQueriesApiSegment,
                ConfigurationKeyName: "ChangeQueriesUrlSegment"
            );

        private readonly Uri _baseAddress;
        private readonly string _connectionName;
        private readonly ILogger _logger;

        public EdFiApiUrlSegmentResolver(Uri baseAddress, string connectionName, ILogger logger = null)
        {
            _baseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
            _connectionName = connectionName;
            _logger = logger ?? Log.ForContext(typeof(EdFiApiUrlSegmentResolver));
        }

        /// <summary>
        /// Resolves one segment, in order of precedence: the value stated on the connection, the value the
        /// API declares in its Discovery document, and the conventional value.
        /// </summary>
        public string Resolve(
            string statedSegment,
            JObject discoveryDocument,
            ApiUrlSegmentDefinition definition
        )
        {
            if (!string.IsNullOrWhiteSpace(statedSegment))
            {
                string normalizedStatedSegment = Normalize(statedSegment);

                _logger.Information(
                    "Using the {ConfigurationKeyName:l} stated for the {ConnectionName:l} connection: '{Segment:l}'. The Discovery document will not be consulted for it.",
                    definition.ConfigurationKeyName,
                    _connectionName,
                    normalizedStatedSegment
                );

                return EnsureNoRoutePlaceholder(normalizedStatedSegment, definition);
            }

            if (
                discoveryDocument?["urls"]?[definition.DiscoveryUrlName]?.ToString() is string declaredUrl
                && !string.IsNullOrWhiteSpace(declaredUrl)
            )
            {
                string declaredSegment = ToRelativeSegment(declaredUrl, _baseAddress, _logger);

                _logger.Debug(
                    "The {ConnectionName:l} API declares {DiscoveryUrlName:l} as '{DeclaredUrl:l}', which is '{Segment:l}' relative to '{BaseAddress}'.",
                    _connectionName,
                    definition.DiscoveryUrlName,
                    declaredUrl,
                    declaredSegment,
                    _baseAddress
                );

                return EnsureNoRoutePlaceholder(declaredSegment, definition);
            }

            // Reached whenever the document could not be read or does not carry this URL, which is ordinary
            // rather than exceptional: an ODS/API publishes changeQueries only while the feature is enabled.
            _logger.Warning(
                "The {ConnectionName:l} API did not declare {DiscoveryUrlName:l} in its Discovery document, so the conventional '{ConventionalSegment:l}' will be used. If this API serves that part of its surface elsewhere, state the path on the connection as {ConfigurationKeyName:l}.",
                _connectionName,
                definition.DiscoveryUrlName,
                definition.ConventionalSegment,
                definition.ConfigurationKeyName
            );

            return Normalize(definition.ConventionalSegment);
        }

        /// <summary>
        /// Expresses a URL declared by the API as a path relative to the connection's own URL, so that a
        /// prefix carried by both is stated once.
        /// </summary>
        public static string ToRelativeSegment(string declaredUrl, Uri baseAddress, ILogger logger = null)
        {
            string declaredPath = Uri.TryCreate(declaredUrl, UriKind.Absolute, out var declaredUri)
                ? declaredUri.AbsolutePath
                : declaredUrl;

            string basePath = baseAddress.AbsolutePath;

            if (!basePath.EndsWith('/'))
            {
                basePath += "/";
            }

            if (declaredPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            {
                return Normalize(declaredPath[basePath.Length..]);
            }

            // The declared URL sits outside the connection's own path, which happens when an API behind a
            // gateway is not told the address its callers reach it by. Its path is the best available answer,
            // but it is the one shape that can go wrong silently, so it is reported.
            if (basePath != "/")
            {
                logger?.Warning(
                    "The API declared '{DeclaredUrl:l}', which is not under the connection URL '{BaseAddress}'. Its path will be used as given. If requests are rejected as not found, state the path on the connection instead.",
                    declaredUrl,
                    baseAddress
                );
            }

            return Normalize(declaredPath);
        }

        /// <summary>
        /// Returns the segment unless it still carries a route placeholder, which means the Discovery document
        /// was read at an address that does not identify a single tenant or instance.
        /// </summary>
        private string EnsureNoRoutePlaceholder(string segment, ApiUrlSegmentDefinition definition)
        {
            if (!ContainsRoutePlaceholder(segment))
            {
                return segment;
            }

            throw new InvalidOperationException(
                $"The {definition.DiscoveryUrlName} path for the {_connectionName} connection resolved to '{segment}', which still carries a route placeholder. {RouteQualifierGuidance}"
            );
        }

        /// <summary>
        /// Reports whether a path taken from a Discovery document still carries an unresolved route
        /// placeholder, such as the <c>{tenant}</c> an API answers with when it is asked for its URLs at an
        /// address that names no tenant.
        /// </summary>
        /// <remarks>
        /// Both spellings are looked for because a placeholder survives a round trip through <see cref="Uri" />
        /// escaped, so searching for the braces alone would let the escaped form through.
        /// </remarks>
        public static bool ContainsRoutePlaceholder(string path)
        {
            string[] placeholderMarkers = ["{", "}", "%7B", "%7D"];

            return path is not null
                && placeholderMarkers.Any(marker => path.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        private static string Normalize(string segment) => segment.Trim('/');
    }
}
