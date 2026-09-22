// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using Serilog;
using System.Text;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Resolves the endpoint a connection's bearer token is requested from, preferring what the connection
    /// states outright, then what the API declares in its Discovery document, and falling back to the
    /// conventional ODS/API path when neither is available.
    /// </summary>
    /// <remarks>
    /// The Discovery API guidelines state that the OAuth URL "must be provided" and that it "is not required
    /// to be on the same base address, i.e. it could be hosted on another server or by a third party", so
    /// unlike the path segments this one is not refused for naming a different host. What a token request
    /// carries is the connection's key and secret, so an endpoint on another host is required to be reached
    /// over HTTPS, and the host it names is reported.
    /// </remarks>
    public class EdFiApiTokenEndpointResolver
    {
        /// <summary>
        /// Gets the path an Ed-Fi API has always been assumed to serve its token endpoint at, relative to the
        /// connection's URL. Used for an API that declares no OAuth URL, or that cannot be asked for one.
        /// </summary>
        public const string ConventionalTokenPath = "oauth/token";

        private const string DiscoveryUrlName = "oauth";

        private readonly Uri _baseAddress;
        private readonly string _connectionName;
        private readonly ILogger _logger;

        public EdFiApiTokenEndpointResolver(Uri baseAddress, string connectionName, ILogger logger = null)
        {
            _baseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
            _connectionName = connectionName;
            _logger = logger ?? Log.ForContext<EdFiApiTokenEndpointResolver>();
        }

        /// <summary>
        /// Returns the absolute URL to request this connection's token from.
        /// </summary>
        /// <param name="statedAuthUrl">The endpoint stated on the connection, or null when none is stated.</param>
        /// <param name="discoveryDocument">The API's Discovery document, as read while the client was built.</param>
        /// <exception cref="InvalidConfigurationException">
        /// Thrown when the stated endpoint is not a URL, or when the API declares one that is not an address
        /// this connection's credentials can be sent to.
        /// </exception>
        public Uri Resolve(string statedAuthUrl, DiscoveryDocument discoveryDocument)
        {
            if (!string.IsNullOrWhiteSpace(statedAuthUrl))
            {
                return FromStatedAuthUrl(statedAuthUrl);
            }

            string declaredUrl = discoveryDocument?.Declares(DiscoveryUrlName);

            if (declaredUrl is null)
            {
                ReportConventionalEndpoint(discoveryDocument);

                return new Uri(_baseAddress, ConventionalTokenPath);
            }

            return FromDeclaredUrl(declaredUrl);
        }

        private Uri FromStatedAuthUrl(string statedAuthUrl)
        {
            if (!Uri.TryCreate(statedAuthUrl, UriKind.Absolute, out var statedUri))
            {
                throw new InvalidConfigurationException(
                    $"The authentication URL stated for the {_connectionName} connection is '{ForLog(statedAuthUrl)}', which is not an absolute URL. Set {ConfigurationPath()} to the full address of the token endpoint, including its scheme."
                );
            }

            _logger?.Information(
                "The {ConnectionName:l} API connection states its token endpoint as '{TokenEndpoint:l}'; its Discovery document is not consulted for one.",
                _connectionName,
                statedUri.AbsoluteUri
            );

            return statedUri;
        }

        /// <summary>
        /// Resolves the URL the API declares, which is where a connection that states nothing sends its key
        /// and secret.
        /// </summary>
        /// <remarks>
        /// Resolved through <see cref="Uri" /> rather than by trimming strings. The guidelines require the
        /// declared URL to be absolute, but one stated relative to the API resolves against the connection's
        /// own address rather than being refused, since that names the same host either way.
        /// </remarks>
        private Uri FromDeclaredUrl(string declaredUrl)
        {
            if (!Uri.TryCreate(_baseAddress, declaredUrl, out var declaredUri))
            {
                throw new InvalidConfigurationException(
                    $"The {DiscoveryUrlName} URL declared by the {_connectionName} API is '{ForLog(declaredUrl)}', which is neither a URL nor a path that can be resolved against the connection URL '{_baseAddress}'. Set {ConfigurationPath()} to the address of its token endpoint."
                );
            }

            if (EdFiApiUrlSegmentResolver.ContainsRoutePlaceholder(declaredUri.ToString()))
            {
                throw new InvalidConfigurationException(
                    $"The {DiscoveryUrlName} URL declared by the {_connectionName} API is '{declaredUri.AbsoluteUri}', which still carries a route placeholder. {EdFiApiUrlSegmentResolver.RouteQualifierGuidance}"
                );
            }

            bool isConnectionsOwnHost =
                Uri.Compare(
                    declaredUri,
                    _baseAddress,
                    UriComponents.SchemeAndServer,
                    UriFormat.UriEscaped,
                    StringComparison.OrdinalIgnoreCase
                ) == 0;

            if (!isConnectionsOwnHost)
            {
                // A token request carries the connection's key and secret, and this address was supplied by
                // the API rather than by the operator. Reaching somewhere other than exactly where this
                // connection already sends its requests is allowed, and is the third-party case the
                // guidelines describe, but not in the clear.
                if (!declaredUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidConfigurationException(
                        $"The {DiscoveryUrlName} URL declared by the {_connectionName} API is '{declaredUri.AbsoluteUri}', which is not the address this connection reaches the API at ('{_baseAddress}') and is not HTTPS. A token request carries this connection's key and secret, and this address came from the API rather than from configuration, so it is only followed over HTTPS. Set {ConfigurationPath()} to state the token endpoint for this connection outright."
                    );
                }

                _logger?.Information(
                    "The {ConnectionName:l} API declares its token endpoint as '{TokenEndpoint:l}', on a different host than the API itself; this connection's key and secret will be sent to {TokenHost}.",
                    _connectionName,
                    declaredUri.AbsoluteUri,
                    declaredUri.Authority
                );

                return declaredUri;
            }

            _logger?.Information(
                "The {ConnectionName:l} API declares its token endpoint as '{TokenEndpoint:l}'.",
                _connectionName,
                declaredUri.AbsoluteUri
            );

            return declaredUri;
        }

        /// <summary>
        /// Says that the conventional path is in use and why, distinguishing an API that answered without
        /// naming its token endpoint from one that could not be asked at all.
        /// </summary>
        private void ReportConventionalEndpoint(DiscoveryDocument discoveryDocument)
        {
            var conventionalEndpoint = new Uri(_baseAddress, ConventionalTokenPath);

            if (discoveryDocument?.WasRead == true)
            {
                _logger?.Information(
                    "The {ConnectionName:l} API declares no {UrlName:l} URL, so its token will be requested from '{TokenEndpoint:l}'.",
                    _connectionName,
                    DiscoveryUrlName,
                    conventionalEndpoint.AbsoluteUri
                );

                return;
            }

            _logger?.Warning(
                "The Discovery document for the {ConnectionName:l} API could not be read, so its token will be requested from '{TokenEndpoint:l}', the path an Ed-Fi API conventionally serves it at.",
                _connectionName,
                conventionalEndpoint.AbsoluteUri
            );
        }

        /// <summary>
        /// Renders a value supplied by the API, or read from configuration, so that it cannot break out of the
        /// line it is written on, and cannot flood the log.
        /// </summary>
        /// <remarks>
        /// Quoting is not the defense it looks like: Serilog escapes a quotation mark inside a string scalar
        /// but writes a newline straight through, and the console and file templates are fixed and public, so
        /// a value carrying a line break forges a line indistinguishable from a real one. A value that has
        /// been through <see cref="Uri" /> is written as its <see cref="Uri.AbsoluteUri" />, which
        /// percent-encodes control characters and is why those are rendered with <c>:l</c>. A value that has
        /// not been parsed is escaped here instead.
        /// </remarks>
        private static string ForLog(string value)
        {
            const int LongestRendered = 200;

            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var rendered = new StringBuilder(Math.Min(value.Length, LongestRendered) + 1);

            foreach (char character in value.Length > LongestRendered ? value[..LongestRendered] : value)
            {
                if (char.IsControl(character))
                {
                    rendered.Append($"%{(int)character:X2}");
                }
                else
                {
                    rendered.Append(character);
                }
            }

            if (value.Length > LongestRendered)
            {
                rendered.Append('…');
            }

            return rendered.ToString();
        }

        private string ConfigurationPath()
        {
            if (string.IsNullOrEmpty(_connectionName))
            {
                return nameof(Configuration.ApiConnectionDetails.AuthUrl);
            }

            string commandLineRole = char.ToLowerInvariant(_connectionName[0]) + _connectionName[1..];

            return $"Connections:{_connectionName}:AuthUrl (--{commandLineRole}AuthUrl)";
        }
    }
}
