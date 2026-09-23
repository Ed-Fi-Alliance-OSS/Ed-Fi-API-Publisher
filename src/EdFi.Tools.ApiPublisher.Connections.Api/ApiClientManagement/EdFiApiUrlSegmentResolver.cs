// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;
using System.Globalization;
using System.Text;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Resolves the path segments the publisher prefixes onto its requests, preferring what the connection
    /// states outright, then what the API declares in its Discovery document, and falling back to the
    /// conventional ODS/API value when neither is available.
    /// </summary>
    /// <remarks>
    /// A declared value is resolved against the connection's own URL and kept relative to it, so that a path
    /// prefix carried by both is stated once. It is resolved with <see cref="Uri" /> rather than by trimming
    /// strings, and refused unless it lands on the connection's own host: the Discovery document is served by
    /// the remote API, so a value taken from it decides where this connection's authenticated requests go.
    /// </remarks>
    public class EdFiApiUrlSegmentResolver
    {
        /// <summary>
        /// Gets the advice shared by everything that refuses a path carrying a route placeholder.
        /// </summary>
        public const string RouteQualifierGuidance =
            "An API that qualifies its routes by tenant resolves them only for an address that names one, so the URL for this connection has to include that prefix (for example 'https://server/tenant1/') rather than the server root. A path can also be set directly on the connection.";

        /// <summary>
        /// Gets the segment that addresses the connection's own URL. Returned in place of an empty string,
        /// because call sites append a resource path that opens with a slash, and an empty segment would make
        /// that path absolute and discard the prefix the connection URL carries.
        /// </summary>
        private const string ConnectionRootSegment = ".";

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
        private readonly int? _schoolYear;
        private readonly ILogger _logger;

        public EdFiApiUrlSegmentResolver(
            Uri baseAddress,
            string connectionName,
            int? schoolYear = null,
            ILogger logger = null
        )
        {
            _baseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
            _connectionName = connectionName;
            _schoolYear = schoolYear;
            _logger = logger ?? Log.ForContext(typeof(EdFiApiUrlSegmentResolver));
        }

        /// <summary>
        /// Resolves one segment, in order of precedence: the value stated on the connection, the value the
        /// API declares in its Discovery document, and the conventional value.
        /// </summary>
        public string Resolve(
            string statedSegment,
            DiscoveryDocument discoveryDocument,
            ApiUrlSegmentDefinition definition
        )
        {
            if (!string.IsNullOrWhiteSpace(statedSegment))
            {
                // Finished before it is announced, because Finish applies the connection's school year and
                // refuses a path still carrying a placeholder. Logging first would report a path the run does
                // not use, and would announce one that is about to be refused.
                // Documented as relative to the connection URL, so a leading slash is dropped rather than
                // resolved against the authority root, where it would silently discard the prefix the
                // connection carries.
                string statedRelativeSegment = Finish(
                    ToRelativeSegment(statedSegment.TrimStart('/'), definition),
                    definition
                );

                _logger.Information(
                    "The {ConnectionName:l} connection states {ConfigurationPath:l}, so requests will use '{Segment:l}' relative to '{BaseAddress}'. Its Discovery document is not consulted for this path.",
                    _connectionName,
                    ConfigurationPathFor(definition),
                    statedRelativeSegment,
                    _baseAddress
                );

                return statedRelativeSegment;
            }

            if (discoveryDocument.Declares(definition.DiscoveryUrlName) is string declaredUrl)
            {
                string declaredSegment = Finish(ToRelativeSegment(declaredUrl, definition), definition);

                // Information rather than Debug: routing now depends on what the API answers, so the path
                // actually in use is the first thing an operator needs when every request comes back 404.
                _logger.Information(
                    "The {ConnectionName:l} API declares {DiscoveryUrlName:l} as '{DeclaredUrl:l}'; requests will use '{Segment:l}' relative to '{BaseAddress}'.",
                    _connectionName,
                    definition.DiscoveryUrlName,
                    ForLog(declaredUrl),
                    declaredSegment,
                    _baseAddress
                );

                return declaredSegment;
            }

            ReportConventionalSegment(discoveryDocument, definition);

            return Finish(Normalize(definition.ConventionalSegment), definition);
        }

        /// <summary>
        /// Renders a value the API supplied so that it cannot break out of the line it is written on, and
        /// cannot flood the log.
        /// </summary>
        /// <remarks>
        /// Quoting is not the defense it looks like: Serilog escapes a quotation mark inside a string scalar
        /// but writes a newline straight through, and the console and file templates are fixed and public, so
        /// a declared value carrying a line break forges a line indistinguishable from a real one. A value
        /// that has been through <see cref="Uri" /> is written as its <see cref="Uri.AbsoluteUri" />, which
        /// percent-encodes control characters; one that has not is escaped here.
        /// </remarks>
        public static string ForLog(string value)
        {
            const int LongestRendered = 200;

            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var rendered = new StringBuilder(Math.Min(value.Length, LongestRendered) + 1);

            foreach (char character in value.Length > LongestRendered ? value[..LongestRendered] : value)
            {
                if (char.IsControl(character) || IsLineOrDirectionMarker(character))
                {
                    // Percent form for the ASCII controls, which reads like the escaping AbsoluteUri
                    // already applies to them. Anything above that gets the \uXXXX form instead, because
                    // "%2028" would read as a space followed by "28" rather than as one character.
                    rendered.Append(
                        character < 0x100 ? $"%{(int)character:X2}" : $@"\u{(int)character:X4}"
                    );
                }
                else
                {
                    rendered.Append(character);
                }
            }

            if (value.Length > LongestRendered)
            {
                rendered.Append("...");
            }

            return rendered.ToString();
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
        /// <summary>
        /// Says whether a character moves the line or its reading direction without being a control
        /// character: the Unicode line and paragraph separators, and the bidirectional overrides that can
        /// make a logged address display as a different one.
        /// </summary>
        private static bool IsLineOrDirectionMarker(char character) =>
            character is '\u2028' or '\u2029'
                or '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E'
                or '\u2066' or '\u2067' or '\u2068' or '\u2069';

        public static bool ContainsRoutePlaceholder(string path)
        {
            string[] placeholderMarkers = ["{", "}", "%7B", "%7D"];

            return path is not null
                && placeholderMarkers.Any(marker => path.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Says which conventional value is in use and why. A target connection is told about change
        /// queries at Debug rather than Information, because a target never reads them and the advice to
        /// state a path it will not use is noise on every run.
        /// </summary>
        private void ReportConventionalSegment(
            DiscoveryDocument discoveryDocument,
            ApiUrlSegmentDefinition definition
        )
        {
            if (discoveryDocument.WasRead)
            {
                // A target never reads change queries, so an ODS/API with the feature disabled would have
                // every target run advised to state a path that run will not use.
                var level = IsUnusedHere(definition) ? LogEventLevel.Debug : LogEventLevel.Information;

                _logger.Write(
                    level,
                    "The {ConnectionName:l} API did not declare {DiscoveryUrlName:l} in its Discovery document, so requests will use the conventional '{ConventionalSegment:l}'. If it serves that path elsewhere, set {ConfigurationPath:l}.",
                    _connectionName,
                    definition.DiscoveryUrlName,
                    definition.ConventionalSegment,
                    ConfigurationPathFor(definition)
                );

                return;
            }

            _logger.Warning(
                "The {ConnectionName:l} API's Discovery document could not be read, so the conventional '{ConventionalSegment:l}' would be used for this path. The publisher takes this API's version information from that same document, so the run will not get past the version check while it cannot be read. Setting {ConfigurationPath:l} corrects a path an API declares wrongly; it does not stand in for a document that cannot be read.",
                _connectionName,
                definition.ConventionalSegment,
                ConfigurationPathFor(definition)
            );
        }

        /// <summary>
        /// Applies the connection's school year, then refuses anything still carrying a route placeholder.
        /// The year goes first so that an API which states the year as a placeholder is answered rather than
        /// rejected.
        /// </summary>
        private string Finish(string segment, ApiUrlSegmentDefinition definition)
        {
            // A segment that climbs out of the connection's own prefix addresses a sibling path, which
            // behind a shared gateway is another application. It is not refused, because the host is the
            // connection's own and the operator may have addressed a tenant while the API serves beneath
            // the root, but it is the one case where requests leave the prefix the operator named.
            if (segment is not null && segment.Split('/').Any(element => element == ".."))
            {
                _logger.Warning(
                    "The {ConnectionName:l} {DiscoveryUrlName:l} path resolves to '{Segment:l}', which is served above the address this connection states ('{BaseAddress}'). Requests, and the credentials on them, will leave that prefix.",
                    _connectionName,
                    definition.DiscoveryUrlName,
                    segment,
                    _baseAddress
                );
            }

            string resolved = EnsureNoRoutePlaceholder(WithSchoolYearApplied(segment, definition), definition);

            return resolved.Length == 0 ? ConnectionRootSegment : resolved;
        }

        /// <summary>
        /// Names the setting an operator would edit, as it is written in a configuration file and on the
        /// command line, rather than the bare property name.
        /// </summary>
        /// <summary>
        /// Says whether this path plays no part in what this connection does, which is the case for change
        /// queries on a target: a target is written to, never read for changes.
        /// </summary>
        private bool IsUnusedHere(ApiUrlSegmentDefinition definition) =>
            definition.DiscoveryUrlName == ChangeQueries.DiscoveryUrlName
            && string.Equals(_connectionName, "Target", StringComparison.OrdinalIgnoreCase);

        private string ConfigurationPathFor(ApiUrlSegmentDefinition definition)
        {
            if (string.IsNullOrEmpty(_connectionName))
            {
                return definition.ConfigurationKeyName;
            }

            string commandLineRole = char.ToLowerInvariant(_connectionName[0]) + _connectionName[1..];

            return $"Connections:{_connectionName}:{definition.ConfigurationKeyName} (--{commandLineRole}{definition.ConfigurationKeyName})";
        }

        /// <summary>
        /// Expresses a URL declared by the API, or stated on the connection, as a path relative to the
        /// connection's own address.
        /// </summary>
        /// <remarks>
        /// Resolved through <see cref="Uri" /> rather than by trimming strings. Trimming a leading slash off a
        /// value the API supplied can turn it into an absolute URL of its own, which an
        /// <see cref="HttpClient" /> then follows in place of its base address, carrying this connection's
        /// bearer token and its request bodies to whatever host the value named.
        /// </remarks>
        private string ToRelativeSegment(string declaredUrl, ApiUrlSegmentDefinition definition)
        {
            if (!Uri.TryCreate(_baseAddress, declaredUrl, out var declaredUri))
            {
                throw new InvalidConfigurationException(
                    $"The {definition.DiscoveryUrlName} path for the {_connectionName} connection is '{ForLog(declaredUrl)}', which is neither a URL nor a path that can be resolved against the connection URL '{_baseAddress}'. Set {ConfigurationPathFor(definition)} to the path its callers use."
                );
            }

            if (!string.Equals(declaredUri.Host, _baseAddress.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidConfigurationException(
                    $"The {definition.DiscoveryUrlName} path for the {_connectionName} connection resolves to '{declaredUri.AbsoluteUri}', which is not served by the host this connection addresses ('{_baseAddress}'). A connection's requests carry its credentials, so they are only ever sent to its own host. If this API is reached through a gateway and declares the address it is deployed at rather than the one its callers use, set {ConfigurationPathFor(definition)} to the path those callers use."
                );
            }

            // Only the path is taken from the declaration, so a scheme or port that disagrees with the
            // connection is worth saying out loud and is not worth refusing over. An ODS/API behind a proxy
            // that terminates TLS, with forwarded headers off, declares http for itself at the very address
            // its callers reach over https; refusing that would stop a deployment that works today.
            if (
                Uri.Compare(
                    declaredUri,
                    _baseAddress,
                    UriComponents.SchemeAndServer,
                    UriFormat.UriEscaped,
                    StringComparison.OrdinalIgnoreCase
                ) != 0
            )
            {
                _logger.Warning(
                    "The {ConnectionName:l} API declares {DiscoveryUrlName:l} at '{DeclaredUrl:l}', which is the host this connection addresses but not over '{ConnectionScheme:l}'. Only the path is taken from it, and requests keep using '{BaseAddress}'. An API behind a proxy that terminates TLS commonly declares this way; correcting the scheme it is told to advertise fixes it for every caller.",
                    _connectionName,
                    definition.DiscoveryUrlName,
                    declaredUri.AbsoluteUri,
                    _baseAddress.Scheme,
                    _baseAddress
                );

                // Restated at the connection's own authority before being made relative, because
                // MakeRelativeUri answers with the absolute URL when the schemes differ, and an absolute
                // segment is exactly what an HttpClient follows in place of its base address.
                declaredUri = new UriBuilder(declaredUri)
                {
                    Scheme = _baseAddress.Scheme,
                    Port = _baseAddress.IsDefaultPort ? -1 : _baseAddress.Port
                }.Uri;
            }

            if (!string.IsNullOrEmpty(declaredUri.Query) || !string.IsNullOrEmpty(declaredUri.Fragment))
            {
                // A relative URI keeps both, and a resource path is appended to this rather than merged with
                // it, so a resource would land inside the query string or the fragment instead of on the
                // server.
                throw new InvalidConfigurationException(
                    $"The {definition.DiscoveryUrlName} path for the {_connectionName} connection resolves to '{declaredUri.AbsoluteUri}', which carries a query string or a fragment. Only a path can be prefixed onto a resource. Set {ConfigurationPathFor(definition)} to the path on its own."
                );
            }

            return Normalize(_baseAddress.MakeRelativeUri(declaredUri).ToString());
        }

        /// <summary>
        /// Applies the connection's school year, for the year-specific routing an ODS/API uses when it serves
        /// more than one school year.
        /// </summary>
        /// <remarks>
        /// A year-specific ODS/API asked for its paths at its unqualified address states a year of its own, and
        /// some versions state an unresolved token in that position instead. The connection's year replaces
        /// whichever it finds, because the operator chose the year and the API only reported the one it
        /// happened to answer with.
        /// </remarks>
        private string WithSchoolYearApplied(string segment, ApiUrlSegmentDefinition definition)
        {
            if (_schoolYear is null)
            {
                return segment;
            }

            string schoolYear = _schoolYear.Value.ToString(CultureInfo.InvariantCulture);
            string[] pathSegments = segment.Split('/');
            string lastPathSegment = pathSegments[^1];

            if (IsSchoolYear(lastPathSegment) || IsSchoolYearPlaceholder(lastPathSegment))
            {
                if (IsSchoolYear(lastPathSegment) && lastPathSegment != schoolYear)
                {
                    _logger.Information(
                        "The {ConnectionName:l} API declares {DiscoveryUrlName:l} for school year {DeclaredSchoolYear:l}, and this connection asks for {SchoolYear:l}. The connection's school year is used.",
                        _connectionName,
                        definition.DiscoveryUrlName,
                        lastPathSegment,
                        schoolYear
                    );
                }

                pathSegments[^1] = schoolYear;

                return string.Join('/', pathSegments);
            }

            return segment.Length == 0 ? schoolYear : $"{segment}/{schoolYear}";
        }

        private static bool IsSchoolYear(string pathSegment) =>
            pathSegment.Length == 4 && pathSegment.All(char.IsAsciiDigit);

        /// <summary>
        /// Reports whether a path segment is the placeholder an API uses for the school year, as opposed to
        /// any other route placeholder. Only this one is a question the connection can answer; a tenant the
        /// connection does not name is a configuration error, and answering it with a school year would turn
        /// a path that cannot be served into one that looks valid and addresses the wrong route.
        /// </summary>
        private static bool IsSchoolYearPlaceholder(string pathSegment)
        {
            if (!ContainsRoutePlaceholder(pathSegment))
            {
                return false;
            }

            string placeholderName = Uri.UnescapeDataString(pathSegment).Trim('{', '}');

            return placeholderName.Equals("schoolYear", StringComparison.OrdinalIgnoreCase);
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

            throw new InvalidConfigurationException(
                $"The {definition.DiscoveryUrlName} path for the {_connectionName} connection resolved to '{segment}', which still carries a route placeholder. {RouteQualifierGuidance}"
            );
        }

        private static string Normalize(string segment) => segment.Trim('/');
    }

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
    /// An API's Discovery document, together with whether it could be read at all. The two outcomes look the
    /// same to a reader of its contents and are not the same thing to an operator: an API that answered
    /// without naming a path is ordinary, while one that could not be asked means the address, or whatever
    /// sits in front of it, is worth checking.
    /// </summary>
    /// <summary>
    /// What came of asking an API for its Discovery document. The distinction between the two failures is
    /// what decides whether a run is reported as a configuration fault or as one that may be repeated.
    /// </summary>
    public enum DiscoveryOutcome
    {
        /// <summary>The API answered with a document.</summary>
        Read,

        /// <summary>The API could not be reached, or answered with a status a later run could get past.</summary>
        Unreachable,

        /// <summary>The API answered, but with something a Discovery document cannot be read from.</summary>
        NotADocument
    }

    public sealed record DiscoveryDocument
    {
        private DiscoveryDocument(JObject content, DiscoveryOutcome outcome)
        {
            Content = content;
            Outcome = outcome;
        }

        /// <summary>
        /// Gets what came of asking the API for its Discovery document. Three outcomes, one value: a pair
        /// of booleans could say both that a document was read and that the API never answered.
        /// </summary>
        public DiscoveryOutcome Outcome { get; }

        public JObject Content { get; }

        /// <summary>
        /// Gets whether the document is there to be read from, which is what every caller asks first.
        /// </summary>
        public bool WasRead => Outcome == DiscoveryOutcome.Read;

        /// <summary>
        /// Returns the document an API answered with.
        /// </summary>
        public static DiscoveryDocument Read(JObject content) =>
            new(content, DiscoveryOutcome.Read);

        /// <summary>
        /// Gets the document to use for an API that could not be reached at all, or that answered with a
        /// status a later run could get past. Such a run may simply be repeated.
        /// </summary>
        public static DiscoveryDocument Unread { get; } =
            new(new JObject(), DiscoveryOutcome.Unreachable);

        /// <summary>
        /// Gets the document to use for an API that answered, but with something its Discovery document
        /// cannot be read from. Whatever is at that address is not serving one, and running again changes
        /// nothing about that.
        /// </summary>
        public static DiscoveryDocument Unusable { get; } =
            new(new JObject(), DiscoveryOutcome.NotADocument);

        /// <summary>
        /// Returns the URL the document declares under <paramref name="urlName" />, or null when it declares
        /// none. The <c>urls</c> member is read as an object rather than indexed directly, because a document
        /// that is not an Ed-Fi Discovery document can carry anything there.
        /// </summary>
        public string Declares(string urlName)
        {
            string declaredUrl = (Content?["urls"] as JObject)?[urlName]?.ToString();

            return string.IsNullOrWhiteSpace(declaredUrl) ? null : declaredUrl;
        }
    }
}
