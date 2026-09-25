// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System;
using System.Linq;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies where the publisher requests a connection's bearer token from (APIPUB-109). The endpoint is
    /// taken from what the connection states, then from the API's own Discovery document, and only then from
    /// the path an Ed-Fi API has conventionally served it at.
    /// </summary>
    /// <remarks>
    /// Unlike the path segments of APIPUB-108, a declared token endpoint is allowed to name a host of its
    /// own, because the Discovery API guidelines say it "could be hosted on another server or by a third
    /// party". A token request carries the connection's key and secret, so that is only followed over HTTPS.
    /// </remarks>
    [TestFixture]
    [NonParallelizable]
    public class EdFiApiTokenEndpointResolutionTests
    {
        private static readonly Uri ServerRoot = new("https://server/");

        [OneTimeSetUp]
        public void ConfigureLogging()
        {
            TestHelpers.InitializeLogging();
        }

        private static DiscoveryDocument DiscoveryDeclaring(params (string Name, string Url)[] urls)
        {
            var declared = new JObject();

            foreach (var (name, url) in urls)
            {
                declared[name] = url;
            }

            return DiscoveryDocument.Read(new JObject { ["urls"] = declared });
        }

        private static EdFiApiTokenEndpointResolver ResolverFor(Uri baseAddress = null) =>
            new(baseAddress ?? ServerRoot, "TestSource");

        [Test]
        public void A_declared_endpoint_on_a_scheme_that_is_not_web_should_be_refused()
        {
            // The host and port comparison treats a default port as a default port whatever the scheme is,
            // so ftp's 21 and https' 443 both read as "default" and the two compare as the same endpoint.
            // Without a scheme check first, this is quietly rewritten to HTTPS instead of being reported.
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor()
                    .Resolve(
                        statedAuthUrl: null,
                        DiscoveryDeclaring(("oauth", "ftp://server/token"))));

            exception.Message.ShouldContain("not an HTTP or HTTPS address");
        }

        [Test]
        public void A_declared_endpoint_on_the_same_host_over_http_should_still_be_reconciled()
        {
            // The negative control for the check above. Refusing every scheme that is not the connection's
            // own would take back the proxy case this branch exists for, so an HTTP declaration against an
            // HTTPS connection has to keep resolving rather than throw.
            var endpoint = ResolverFor()
                .Resolve(
                    statedAuthUrl: null,
                    DiscoveryDeclaring(("oauth", "http://server/token")));

            endpoint.Scheme.ShouldBe("https");
            endpoint.AbsoluteUri.ShouldBe("https://server/token");
        }

        [Test]
        public void A_declared_endpoint_should_be_used_as_declared()
        {
            // Deliberately not the conventional path, so that falling back rather than reading the document
            // would show up here.
            var endpoint = ResolverFor()
                .Resolve(
                    statedAuthUrl: null,
                    DiscoveryDeclaring(("oauth", "https://server/identity/connect/token"))
                );

            endpoint.ShouldBe(new Uri("https://server/identity/connect/token"));
        }

        [Test]
        public void A_declared_endpoint_should_keep_the_prefix_the_connection_url_carries()
        {
            // A DMS served beneath a path base declares that base in every URL it publishes.
            // Deliberately not the conventional path under that prefix: asserting
            // "https://server/api/oauth/token" would be asserting exactly what the fallback composes, so this
            // would pass even if the declared value were never read.
            var endpoint = ResolverFor(new Uri("https://server/api/"))
                .Resolve(
                    statedAuthUrl: null,
                    DiscoveryDeclaring(("oauth", "https://server/api/identity/connect/token"))
                );

            endpoint.ShouldBe(new Uri("https://server/api/identity/connect/token"));
        }

        [Test]
        public void A_stated_authentication_url_should_win_over_a_declared_one()
        {
            var endpoint = ResolverFor()
                .Resolve(
                    "https://keycloak/realms/edfi/protocol/openid-connect/token",
                    DiscoveryDeclaring(("oauth", "https://server/oauth/token"))
                );

            endpoint.ShouldBe(new Uri("https://keycloak/realms/edfi/protocol/openid-connect/token"));
        }

        [Test]
        public void An_endpoint_declared_on_another_host_over_https_should_be_used()
        {
            // The guidelines allow the OAuth URL to be served by another server or a third party.
            var endpoint = ResolverFor()
                .Resolve(
                    statedAuthUrl: null,
                    DiscoveryDeclaring(("oauth", "https://identity.example/connect/token"))
                );

            endpoint.ShouldBe(new Uri("https://identity.example/connect/token"));
        }

        [Test]
        public void An_endpoint_on_another_host_should_be_refused_when_certificates_are_not_verified()
        {
            // HTTPS is what makes another host followable, because the certificate says the host reached is
            // the host named. With --ignoreSslErrors the handler accepts any certificate, so nothing says
            // that, and the one guarantee behind following an address the API chose is gone.
            var exception = Should.Throw<InvalidConfigurationException>(
                () =>
                    new EdFiApiTokenEndpointResolver(
                            ServerRoot,
                            "Source",
                            serverCertificatesUnverified: true)
                        .Resolve(
                            statedAuthUrl: null,
                            DiscoveryDeclaring(("oauth", "https://identity.example/connect/token"))
                        )
            );

            exception.Message.ShouldContain("verify server certificates");
            exception.Message.ShouldContain("--sourceAuthUrl");
        }

        [Test]
        public void The_apis_own_address_should_still_be_used_when_certificates_are_not_verified()
        {
            // The refusal is about an address the API chose, not about the connection's own.
            var endpoint = new EdFiApiTokenEndpointResolver(
                    ServerRoot,
                    "TestSource",
                    serverCertificatesUnverified: true)
                .Resolve(statedAuthUrl: null, DiscoveryDeclaring(("oauth", "https://server/identity/connect/token")));

            endpoint.ShouldBe(new Uri("https://server/identity/connect/token"));
        }

        [Test]
        public void An_endpoint_declared_on_another_host_without_https_should_be_refused()
        {
            // A token request carries the connection's key and secret, and this address came from the API
            // rather than from configuration.
            var exception = Should.Throw<InvalidConfigurationException>(
                () =>
                    ResolverFor()
                        .Resolve(
                            statedAuthUrl: null,
                            DiscoveryDeclaring(("oauth", "http://attacker.example/collect"))
                        )
            );

            exception.Message.ShouldContain("attacker.example");
            exception.Message.ShouldContain("HTTPS");
        }

        [Test]
        public void A_refusal_should_name_the_setting_that_corrects_it()
        {
            // The connection names used in a run are "Source" and "Target", which is what the command line
            // argument is built from.
            var exception = Should.Throw<InvalidConfigurationException>(
                () =>
                    new EdFiApiTokenEndpointResolver(ServerRoot, "Source").Resolve(
                        statedAuthUrl: null,
                        DiscoveryDeclaring(("oauth", "http://identity.example/connect/token"))
                    )
            );

            exception.Message.ShouldContain("Connections:Source:AuthUrl");
            exception.Message.ShouldContain("--sourceAuthUrl");
        }

        [Test]
        public void An_endpoint_declared_over_plain_http_at_the_connections_own_host_should_be_used_over_https()
        {
            // An ODS/API behind a proxy that terminates TLS, with forwarded headers off, declares http for
            // itself at the very address its callers reach over https. That deployment works today, and the
            // token is the first request a run makes, so refusing it would stop the run before anything else.
            // The connection's https is kept, so a declaration cannot move credentials onto plain HTTP.
            var endpoint = ResolverFor()
                .Resolve(statedAuthUrl: null, DiscoveryDeclaring(("oauth", "http://server/oauth/token")));

            endpoint.ShouldBe(new Uri("https://server/oauth/token"));
        }

        [Test]
        public void An_endpoint_declared_over_https_should_not_be_downgraded_to_the_connections_http()
        {
            // The mirror image: taking the connection's scheme unconditionally would move a token request
            // that the API offers over HTTPS onto plain HTTP.
            var endpoint = ResolverFor(new Uri("http://server/"))
                .Resolve(statedAuthUrl: null, DiscoveryDeclaring(("oauth", "https://server/oauth/token")));

            endpoint.ShouldBe(new Uri("https://server/oauth/token"));
        }

        [Test]
        public void A_scheme_disagreement_at_the_connections_own_host_should_be_reported()
        {
            using (TestCorrelator.CreateContext())
            {
                ResolverFor()
                    .Resolve(statedAuthUrl: null, DiscoveryDeclaring(("oauth", "http://server/oauth/token")));

                var notice = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Single(e => e.MessageTemplate.Text.Contains("not the scheme it uses"));

                notice.Level.ShouldBe(LogEventLevel.Warning);
                notice.RenderMessage().ShouldContain("proxy");
            }
        }

        [Test]
        public void An_endpoint_declared_over_https_at_the_connections_own_host_should_not_be_reported_as_another_host()
        {
            // A connection reached over plain HTTP whose API declares HTTPS for itself is not the
            // third-party case and should not be announced as one.
            using (TestCorrelator.CreateContext())
            {
                var endpoint = ResolverFor(new Uri("http://server/"))
                    .Resolve(statedAuthUrl: null, DiscoveryDeclaring(("oauth", "https://server/identity/connect/token")));

                endpoint.ShouldBe(new Uri("https://server/identity/connect/token"));

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .ShouldNotContain(e => e.MessageTemplate.Text.Contains("on a different host"));
            }
        }

        [Test]
        public void An_endpoint_declared_on_another_host_should_be_allowed_when_the_connection_is_plain_http()
        {
            // An http connection to an https identity provider is still an https token request.
            var endpoint = ResolverFor(new Uri("http://localhost:8090/api/"))
                .Resolve(
                    statedAuthUrl: null,
                    DiscoveryDeclaring(("oauth", "https://identity.example/connect/token"))
                );

            endpoint.ShouldBe(new Uri("https://identity.example/connect/token"));
        }

        [Test]
        public void An_endpoint_still_carrying_a_route_placeholder_should_be_refused()
        {
            var exception = Should.Throw<InvalidConfigurationException>(
                () =>
                    ResolverFor()
                        .Resolve(
                            statedAuthUrl: null,
                            DiscoveryDeclaring(("oauth", "https://server/{tenant}/oauth/token"))
                        )
            );

            exception.Message.ShouldContain("route placeholder");
        }

        [Test]
        public void An_api_declaring_no_endpoint_should_fall_back_to_the_conventional_path()
        {
            var endpoint = ResolverFor()
                .Resolve(statedAuthUrl: null, DiscoveryDeclaring(("dataManagementApi", "https://server/data/v3/")));

            endpoint.ShouldBe(new Uri("https://server/oauth/token"));
        }

        [Test]
        public void An_api_that_could_not_be_asked_should_fall_back_to_the_conventional_path()
        {
            var endpoint = ResolverFor(new Uri("https://server/api/"))
                .Resolve(statedAuthUrl: null, DiscoveryDocument.Unread);

            endpoint.ShouldBe(new Uri("https://server/api/oauth/token"));
        }

        [Test]
        public void A_stated_authentication_url_that_is_not_absolute_should_be_refused()
        {
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor().Resolve("oauth/token", DiscoveryDocument.Unread)
            );

            exception.Message.ShouldContain("absolute");
        }

        [Test]
        public void A_declared_endpoint_should_not_be_able_to_forge_a_log_line()
        {
            // A value carrying a line break survives Uri.TryCreate, the placeholder check and the authority
            // comparison, and Uri.ToString() renders the break intact. The console and file templates are
            // fixed and public, so that forges a line indistinguishable from a real one. Serilog quoting is
            // not the defense: it escapes a quotation mark, not a newline.
            using (TestCorrelator.CreateContext())
            {
                ResolverFor()
                    .Resolve(
                        statedAuthUrl: null,
                        DiscoveryDeclaring(("oauth", "https://server/oauth\r\n[FTL] Processing complete/token"))
                    );

                string rendered = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Single(e => e.MessageTemplate.Text.Contains("declares its token endpoint"))
                    .RenderMessage();

                rendered.ShouldNotContain("\n");
                rendered.ShouldNotContain("\r");
                rendered.ShouldContain("%0D%0A");
            }
        }

        [Test]
        public void A_stated_endpoint_that_cannot_be_parsed_should_not_be_able_to_forge_a_log_line()
        {
            // This one never reaches Uri at all, so the escaping has to happen where the message is built.
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor().Resolve("not a url\r\n[FTL] Processing complete", DiscoveryDocument.Unread)
            );

            exception.Message.ShouldNotContain("\n");
            exception.Message.ShouldNotContain("\r");
        }

        [Test]
        public void A_stated_endpoint_on_another_host_over_plain_http_should_be_used()
        {
            // This is the remedy the refusal message and the documentation both name. If the HTTPS rule were
            // ever hoisted ahead of the stated branch, the documented escape would start refusing too.
            var endpoint = ResolverFor()
                .Resolve(
                    "http://identity.example/connect/token",
                    DiscoveryDeclaring(("oauth", "https://server/oauth/token"))
                );

            endpoint.ShouldBe(new Uri("http://identity.example/connect/token"));
        }

        [Test]
        public void A_stated_endpoint_should_keep_the_path_it_was_given()
        {
            // Earlier versions applied a trailing slash before requesting the token, which against a server
            // that distinguishes the two is a different request.
            var endpoint = ResolverFor().Resolve("https://idp/connect/token", DiscoveryDocument.Unread);

            endpoint.AbsoluteUri.ShouldBe("https://idp/connect/token");
        }

        [Test]
        public void A_stated_endpoint_that_is_not_a_web_address_should_be_refused()
        {
            // Absolute is not enough: file:// and ftp:// parse, and then surface from inside HttpClient as a
            // NotSupportedException that reads as a defect in the tool rather than as configuration, missing
            // the exit code that tells an operator what to correct.
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor().Resolve("ftp://identity.example/token", DiscoveryDocument.Unread)
            );

            exception.Message.ShouldContain("HTTP or HTTPS");
            exception.Message.ShouldContain("--sourceAuthUrl".Replace("source", "testSource"));
        }

        [Test]
        public void A_connection_url_without_a_trailing_slash_should_not_move_the_endpoint()
        {
            // Resolving a relative value against an address with no trailing slash drops its last path
            // segment, so "https://server/api" would put the token endpoint at the server root. The one
            // caller happens to normalize before constructing this; the resolver should not rely on that.
            var conventional = new EdFiApiTokenEndpointResolver(new Uri("https://server/api"), "TestSource")
                .Resolve(statedAuthUrl: null, DiscoveryDocument.Unread);

            conventional.ShouldBe(new Uri("https://server/api/oauth/token"));

            var declared = new EdFiApiTokenEndpointResolver(new Uri("https://server/api"), "TestSource")
                .Resolve(statedAuthUrl: null, DiscoveryDeclaring(("oauth", "identity/connect/token")));

            declared.ShouldBe(new Uri("https://server/api/identity/connect/token"));
        }

        [Test]
        public void Userinfo_in_an_endpoint_should_not_be_written_to_the_log()
        {
            // An operator who puts credentials in the URL should not find them in the log, and this is the
            // change that newly writes that URL down.
            using (TestCorrelator.CreateContext())
            {
                var endpoint = ResolverFor().Resolve("https://user:secret@idp/token", DiscoveryDocument.Unread);

                // The value itself is left as given; it is only kept out of the log.
                endpoint.UserInfo.ShouldBe("user:secret");

                string rendered = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Single(e => e.MessageTemplate.Text.Contains("token will be requested"))
                    .RenderMessage();

                rendered.ShouldNotContain("secret");
                rendered.ShouldContain("idp");
            }
        }

        [Test]
        public void A_blank_stated_endpoint_should_be_treated_as_none()
        {
            var endpoint = ResolverFor()
                .Resolve("   ", DiscoveryDeclaring(("oauth", "https://server/identity/connect/token")));

            endpoint.ShouldBe(new Uri("https://server/identity/connect/token"));
        }

        [Test]
        public void Credentials_leaving_the_own_host_of_the_api_should_be_reported()
        {
            // The only signal an operator gets that the key and secret are going somewhere the API nominated.
            using (TestCorrelator.CreateContext())
            {
                ResolverFor()
                    .Resolve(
                        statedAuthUrl: null,
                        DiscoveryDeclaring(("oauth", "https://identity.example/connect/token"))
                    );

                var offHostNotice = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Single(e => e.MessageTemplate.Text.Contains("different host"));

                offHostNotice.Level.ShouldBe(LogEventLevel.Information);
                offHostNotice.RenderMessage().ShouldContain("identity.example");
            }
        }

        [Test]
        public void A_document_that_could_not_be_read_should_be_reported_differently_from_one_that_said_nothing()
        {
            using (TestCorrelator.CreateContext())
            {
                ResolverFor().Resolve(statedAuthUrl: null, DiscoveryDocument.Unread);

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .ShouldContain(e => e.Level == LogEventLevel.Warning);
            }

            using (TestCorrelator.CreateContext())
            {
                ResolverFor()
                    .Resolve(
                        statedAuthUrl: null,
                        DiscoveryDeclaring(("dataManagementApi", "https://server/data/v3/"))
                    );

                var events = TestCorrelator.GetLogEventsFromCurrentContext().ToList();

                events.ShouldContain(e => e.Level == LogEventLevel.Information);
                events.ShouldNotContain(e => e.Level == LogEventLevel.Warning);
            }
        }

        [Test]
        public void A_declared_endpoint_stated_relative_to_the_api_should_resolve_against_the_connection()
        {
            // The guidelines require an absolute URL. One stated relative names the same host either way, so
            // it is resolved rather than refused.
            var endpoint = ResolverFor(new Uri("https://server/api/"))
                .Resolve(statedAuthUrl: null, DiscoveryDeclaring(("oauth", "/api/identity/connect/token")));

            endpoint.ShouldBe(new Uri("https://server/api/identity/connect/token"));
        }
    }
}
