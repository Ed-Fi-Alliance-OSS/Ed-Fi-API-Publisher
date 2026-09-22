// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
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
        /// The path an Ed-Fi API has always been assumed to serve its token endpoint at, relative to the
        /// connection's URL. Used for an API that declares no OAuth URL, or that cannot be asked for one.
        /// </summary>
        private const string ConventionalTokenPath = "oauth/token";

        private const string DiscoveryUrlName = "oauth";

        private const string AuthUrlSettingName = "AuthUrl";

        private readonly Uri _baseAddress;
        private readonly string _connectionName;
        private readonly ILogger _logger;

        /// <param name="baseAddress">The connection's URL. Normalized to end in a slash, because resolving a
        /// relative value against an address that does not drops its last path segment, which would move both
        /// the conventional endpoint and any endpoint declared relative to the API.</param>
        public EdFiApiTokenEndpointResolver(Uri baseAddress, string connectionName, ILogger logger = null)
        {
            ArgumentNullException.ThrowIfNull(baseAddress);

            _baseAddress = new Uri(baseAddress.AbsoluteUri.EnsureSuffixApplied("/"));

            _connectionName = connectionName;
            _logger = logger ?? Log.ForContext(typeof(EdFiApiTokenEndpointResolver));
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

            string declaredUrl = discoveryDocument.Declares(DiscoveryUrlName);

            if (declaredUrl is null)
            {
                return ConventionalEndpointReported(discoveryDocument);
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

            if (!IsWebScheme(statedUri))
            {
                throw new InvalidConfigurationException(
                    $"The authentication URL stated for the {_connectionName} connection is '{ForLog(statedAuthUrl)}', which is not an HTTP or HTTPS address. Set {ConfigurationPath()} to the address of the token endpoint."
                );
            }

            _logger.Information(
                "The {ConnectionName:l} connection states {ConfigurationKey:l}, so its token will be requested from '{TokenEndpoint:l}' and its Discovery document is not consulted for one.",
                _connectionName,
                ConfigurationPath(),
                ForLog(statedUri)
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
                    $"The {DiscoveryUrlName} URL declared by the {_connectionName} API is '{ForLog(declaredUri)}', which still carries a route placeholder. {EdFiApiUrlSegmentResolver.RouteQualifierGuidance} For the token endpoint, set {ConfigurationPath()} to its full address."
                );
            }

            // Host and port are compared apart from the scheme, because the two differences are not the same
            // situation and an operator reading about the wrong one goes looking for something that is not
            // there. An API behind a proxy that terminates TLS without forwarding the original scheme
            // declares http for itself at the very address its callers reach over https, which is a common
            // deployment and nothing to do with another host.
            // Not UriComponents.HostAndPort, which resolves each side's default port from its own scheme and
            // so reports 80 against 443 for the very case this is here to recognize. Two default ports are
            // treated as the same endpoint; two stated ports are not.
            bool isSameEndpointHost =
                string.Equals(declaredUri.Host, _baseAddress.Host, StringComparison.OrdinalIgnoreCase)
                && (
                    declaredUri.Port == _baseAddress.Port
                    || (declaredUri.IsDefaultPort && _baseAddress.IsDefaultPort)
                );

            bool isConnectionsOwnAddress =
                Uri.Compare(
                    declaredUri,
                    _baseAddress,
                    UriComponents.SchemeAndServer,
                    UriFormat.UriEscaped,
                    StringComparison.OrdinalIgnoreCase
                ) == 0;

            if (isConnectionsOwnAddress)
            {
                _logger.Information(
                    "The {ConnectionName:l} API declares its token endpoint as '{TokenEndpoint:l}'.",
                    _connectionName,
                    ForLog(declaredUri)
                );

                return declaredUri;
            }

            // A token request carries the connection's key and secret, and this address was supplied by the
            // API rather than by the operator. Anywhere other than the address this connection already
            // reaches the API at is allowed, and is the third-party case the guidelines describe, but not in
            // the clear.
            if (!declaredUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidConfigurationException(
                    isSameEndpointHost
                        ? $"The {DiscoveryUrlName} URL declared by the {_connectionName} API is '{ForLog(declaredUri)}', which is the host this connection addresses but over plain HTTP rather than the {_baseAddress.Scheme.ToUpperInvariant()} this connection uses. A token request carries this connection's key and secret, so it is not sent in the clear. An API behind a proxy that terminates TLS commonly declares HTTP for itself; correcting the scheme the proxy forwards fixes this for every caller. To override it for this connection alone, set {ConfigurationPath()}."
                        : $"The {DiscoveryUrlName} URL declared by the {_connectionName} API is '{ForLog(declaredUri)}', which is served by neither the address this connection reaches the API at ('{_baseAddress}') nor over HTTPS. A token request carries this connection's key and secret, and this address came from the API rather than from configuration, so another host is only reached over HTTPS. Set {ConfigurationPath()} to state the token endpoint for this connection outright."
                );
            }

            if (isSameEndpointHost)
            {
                // Same host and port, reached over HTTPS where the connection itself is not. Worth saying,
                // but it is not the third-party case and should not be reported as one.
                _logger.Information(
                    "The {ConnectionName:l} API declares its token endpoint as '{TokenEndpoint:l}', on the host this connection addresses but over HTTPS.",
                    _connectionName,
                    ForLog(declaredUri)
                );

                return declaredUri;
            }

            _logger.Information(
                "The {ConnectionName:l} API declares its token endpoint as '{TokenEndpoint:l}', on a different host than the API itself; this connection's key and secret will be sent to {TokenHost}.",
                _connectionName,
                ForLog(declaredUri),
                declaredUri.Authority
            );

            return declaredUri;
        }

        /// <summary>
        /// Says that the conventional path is in use and why, distinguishing an API that answered without
        /// naming its token endpoint from one that could not be asked at all.
        /// </summary>
        private Uri ConventionalEndpointReported(DiscoveryDocument discoveryDocument)
        {
            var conventionalEndpoint = new Uri(_baseAddress, ConventionalTokenPath);

            if (discoveryDocument.WasRead)
            {
                _logger.Information(
                    "The {ConnectionName:l} API declares no {UrlName:l} URL, so its token will be requested from '{TokenEndpoint:l}'. If it serves its token endpoint elsewhere, set {ConfigurationKey:l}.",
                    _connectionName,
                    DiscoveryUrlName,
                    ForLog(conventionalEndpoint),
                    ConfigurationPath()
                );

                return conventionalEndpoint;
            }

            _logger.Warning(
                "The Discovery document for the {ConnectionName:l} API could not be read, so its token will be requested from '{TokenEndpoint:l}', the path an Ed-Fi API conventionally serves it at. The publisher takes this API's version information from that same document, so the run will not get past the version check while it cannot be read.",
                _connectionName,
                ForLog(conventionalEndpoint)
            );

            return conventionalEndpoint;
        }

        /// <summary>
        /// Says whether an endpoint is one the publisher can actually send a token request to.
        /// </summary>
        private static bool IsWebScheme(Uri endpoint) =>
            endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Renders an endpoint for the log without the userinfo an operator may have put in it, since
        /// <see cref="Uri.AbsoluteUri" /> carries that through and it is a credential of its own.
        /// </summary>
        /// <remarks>
        /// It is left in the value that is used, because it is the address the operator or the API gave; it
        /// simply has no business being written down. <see cref="System.Net.Http.HttpClient" /> does not act
        /// on it in any case, since the publisher sets its own authorization header.
        /// </remarks>
        private static string ForLog(Uri endpoint) =>
            string.IsNullOrEmpty(endpoint.UserInfo)
                ? endpoint.AbsoluteUri
                : endpoint.GetComponents(
                    UriComponents.AbsoluteUri & ~UriComponents.UserInfo,
                    UriFormat.UriEscaped
                );

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
                return AuthUrlSettingName;
            }

            string commandLineRole = char.ToLowerInvariant(_connectionName[0]) + _connectionName[1..];

            return $"Connections:{_connectionName}:{AuthUrlSettingName} (--{commandLineRole}{AuthUrlSettingName})";
        }
    }
}
