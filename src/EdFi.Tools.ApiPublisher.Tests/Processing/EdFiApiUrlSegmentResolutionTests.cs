// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies how the publisher decides where an API serves its data management and change queries
    /// resources (APIPUB-108). The path is taken from what the connection states, then from the API's own
    /// Discovery document, and only then from the value the publisher has conventionally assumed. An
    /// ODS/API and a DMS declare the same names but not the same paths, so the declared value is what
    /// decides, and it is held relative to the connection's URL so a prefix carried by both is stated once.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class EdFiApiUrlSegmentResolutionTests
    {
        [OneTimeSetUp]
        public void ConfigureLogging()
        {
            // Configured for the fixture rather than from inside two of its tests: this replaces the static
            // Serilog logger the whole assembly shares, and the resolver reads that logger when none is
            // passed, so setting it part-way through a run couples those tests to the order they run in.
            TestHelpers.InitializeLogging();
        }

        private static readonly Uri ServerRoot = new("https://server/");

        private static DiscoveryDocument DiscoveryDeclaring(params (string Name, string Url)[] urls)
        {
            var declared = new JObject();

            foreach (var (name, url) in urls)
            {
                declared[name] = url;
            }

            return DiscoveryDocument.Read(new JObject { ["urls"] = declared });
        }

        private static EdFiApiUrlSegmentResolver ResolverFor(Uri baseAddress = null, int? schoolYear = null) =>
            new(baseAddress ?? ServerRoot, "TestSource", schoolYear);

        [Test]
        public void A_declared_path_should_not_be_able_to_move_a_log_line_without_a_control_character()
        {
            // U+2028 and U+2029 are line and paragraph separators that char.IsControl does not report, and
            // the bidi overrides can make a logged address display as a different one.
            using (TestCorrelator.CreateContext())
            {
                Should.Throw<InvalidConfigurationException>(
                    () => ResolverFor()
                        .Resolve(
                            statedSegment: null,
                            DiscoveryDeclaring(("dataManagementApi", "https://elsewhere.example/\u2028\u202Ecollect")),
                            EdFiApiUrlSegmentResolver.DataManagement));
            }

            string rendered = EdFiApiUrlSegmentResolver.ForLog("a\u2028b\u202Ec");

            rendered.ShouldNotContain("\u2028");
            rendered.ShouldNotContain("\u202E");
            rendered.ShouldContain(@"\u2028");
        }

        [Test]
        public void A_declared_path_should_not_be_able_to_forge_a_log_line()
        {
            // A value carrying a line break survives Uri.TryCreate and the authority comparison, and
            // Serilog writes a newline straight through a quoted string scalar. The console and file
            // templates are fixed and public, so that forges a line indistinguishable from a real one.
            using (TestCorrelator.CreateContext())
            {
                ResolverFor()
                    .Resolve(
                        statedSegment: null,
                        DiscoveryDeclaring(("dataManagementApi", "https://server/data\r\n[FTL] Processing complete/v3/")),
                        EdFiApiUrlSegmentResolver.DataManagement);

                string rendered = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Single(e => e.MessageTemplate.Text.Contains("declares"))
                    .RenderMessage();

                rendered.ShouldNotContain("\n");
                rendered.ShouldNotContain("\r");
            }
        }

        [Test]
        public void A_declared_trailing_slash_should_be_dropped()
        {
            // Call sites append a resource path that already opens with a slash, so a segment that kept its
            // own would build "changes//students". The declared value is deliberately not the conventional
            // one, so that falling back rather than resolving would show up here.
            var segment = ResolverFor()
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("changeQueries", "https://server/changes/")),
                    EdFiApiUrlSegmentResolver.ChangeQueries);

            segment.ShouldBe("changes");
        }

        [Test]
        public void A_declaration_differing_only_in_scheme_should_be_used()
        {
            // An ODS/API behind a proxy that terminates TLS, with forwarded headers off, declares http for
            // itself at the very address its callers reach over https. That deployment works today, because
            // the path was assumed; refusing it here would stop it on upgrade.
            var segment = ResolverFor(new Uri("https://server/"))
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("dataManagementApi", "http://server/data/v3/")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            segment.ShouldBe("data/v3");
        }

        [Test]
        public void A_declaration_differing_only_in_scheme_should_not_produce_an_absolute_segment()
        {
            // The reason the case above needs its own guard: MakeRelativeUri answers with the absolute URL
            // when the schemes differ, and a segment that is absolute is what an HttpClient follows in place
            // of its base address, which is the whole failure this resolver exists to prevent.
            var segment = ResolverFor(new Uri("https://server/"))
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("dataManagementApi", "http://server/data/v3/")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            Uri.TryCreate(segment, UriKind.Absolute, out _).ShouldBeFalse();
            segment.ShouldNotContain("://");
        }

        [Test]
        public void A_declaration_differing_only_in_port_should_be_used()
        {
            // A proxy exposing the API on one port while the API declares the port it listens on.
            var segment = ResolverFor(new Uri("https://server/"))
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("dataManagementApi", "https://server:8080/data/v3/")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            segment.ShouldBe("data/v3");
        }

        [Test]
        public void A_declaration_differing_only_in_scheme_should_be_reported()
        {
            // It is not refused, so the log is the only place an operator learns their API is advertising
            // itself at an address its callers do not use.
            using (TestCorrelator.CreateContext())
            {
                ResolverFor(new Uri("https://server/"))
                    .Resolve(
                        statedSegment: null,
                        DiscoveryDeclaring(("dataManagementApi", "http://server/data/v3/")),
                        EdFiApiUrlSegmentResolver.DataManagement);

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .ShouldContain(e =>
                        e.Level == LogEventLevel.Warning
                        && e.MessageTemplate.Text.Contains("not over"));
            }
        }

        [Test]
        public void The_reported_path_should_be_the_one_requests_use()
        {
            // The school year is applied after the segment is resolved, so reporting the intermediate value
            // told an operator debugging a 404 that requests use a path they do not.
            using (TestCorrelator.CreateContext())
            {
                var segment = ResolverFor(schoolYear: 2024)
                    .Resolve(
                        statedSegment: null,
                        DiscoveryDeclaring(("dataManagementApi", "https://server/data/v3/")),
                        EdFiApiUrlSegmentResolver.DataManagement);

                segment.ShouldBe("data/v3/2024");

                string reported = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Single(e => e.MessageTemplate.Text.Contains("requests will use"))
                    .RenderMessage();

                reported.ShouldContain("data/v3/2024");
            }
        }

        [Test]
        public void A_stated_path_should_be_read_as_relative_even_with_a_leading_slash()
        {
            // Documented as relative to the connection URL. Resolved as given, a leading slash addresses the
            // authority root and silently discards the prefix the connection carries.
            var segment = ResolverFor(new Uri("https://server/tenant1/"))
                .Resolve(
                    statedSegment: "/data/v3",
                    DiscoveryDocument.Unread,
                    EdFiApiUrlSegmentResolver.DataManagement);

            segment.ShouldBe("data/v3");
        }

        [Test]
        public void A_path_served_above_the_connection_should_be_reported()
        {
            // Not refused: the host is the connection's own, and an operator may have addressed a tenant
            // while the API serves beneath the root. But it is the one case where requests, and the
            // credentials on them, leave the prefix the operator named.
            using (TestCorrelator.CreateContext())
            {
                var segment = ResolverFor(new Uri("https://gw/tenantA/"))
                    .Resolve(
                        statedSegment: null,
                        DiscoveryDeclaring(("dataManagementApi", "https://gw/other-app/data")),
                        EdFiApiUrlSegmentResolver.DataManagement);

                segment.ShouldBe("../other-app/data");

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .ShouldContain(e =>
                        e.Level == LogEventLevel.Warning
                        && e.MessageTemplate.Text.Contains("served above the address this connection states"));
            }
        }

        [Test]
        public void A_declaration_on_another_host_should_be_refused()
        {
            // The Discovery document is served by the remote API, and a connection's requests carry its
            // credentials. Following a declaration off the connection's own host would send this API's bearer
            // token, and the documents being published, somewhere the operator never named.
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor()
                    .Resolve(
                        statedSegment: null,
                        DiscoveryDeclaring(("dataManagementApi", "https://elsewhere.example/collect")),
                        EdFiApiUrlSegmentResolver.DataManagement));

            exception.Message.ShouldContain("not served by the host this connection addresses");
        }

        [Test]
        public void A_declaration_that_would_read_as_an_absolute_URL_once_trimmed_should_stay_on_the_host()
        {
            // Resolving by trimming a leading slash turns this value into an absolute URL, which an HttpClient
            // follows in place of its base address. Resolving it against the connection URL instead keeps it a
            // path on the connection's own host, where it can only fail to be found.
            var segment = ResolverFor()
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("dataManagementApi", "/https://elsewhere.example/collect")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            new Uri(ServerRoot, $"{segment}/ed-fi/students").Host.ShouldBe(ServerRoot.Host);
        }

        [Test]
        public void A_segment_stated_on_the_connection_for_another_host_should_be_refused()
        {
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor()
                    .Resolve(
                        statedSegment: "https://elsewhere.example/collect",
                        DiscoveryDeclaring(("dataManagementApi", "https://server/data")),
                        EdFiApiUrlSegmentResolver.DataManagement));

            exception.Message.ShouldContain("not served by the host this connection addresses");
        }

        [Test]
        public void A_declared_school_year_should_be_replaced_by_the_connection_school_year()
        {
            // A year-specific ODS/API asked at its unqualified address declares a year of its own. Appending
            // the connection's year to that would address a year within a year, and every request would miss.
            var segment = ResolverFor(new Uri("https://localhost/WebApi/"), schoolYear: 2024)
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("dataManagementApi", "https://localhost/WebApi/data/v3/2025")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            segment.ShouldBe("data/v3/2024");
        }

        [Test]
        public void A_declared_school_year_placeholder_should_be_answered_rather_than_refused()
        {
            // Some ODS/API versions state the year position as an unresolved token. The connection names the
            // year, so this is a question the publisher can answer instead of a reason to stop.
            var segment = ResolverFor(schoolYear: 2024)
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("dataManagementApi", "https://server/data/v3/{schoolYear}")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            segment.ShouldBe("data/v3/2024");
        }

        [Test]
        public void A_DMS_declaring_a_different_path_should_resolve_to_that_path()
        {
            // The case the ticket exists for: DMS serves "/data", not "/data/v3". Falling back to the
            // conventional value here would give "data/v3" and every request would miss.
            var segment = ResolverFor()
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("dataManagementApi", "https://server/data")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            segment.ShouldBe("data");
            segment.ShouldNotBe(EdFiApiConstants.DataManagementApiSegment);
        }

        [Test]
        public void A_path_prefix_carried_by_both_the_connection_and_the_declaration_should_be_stated_once()
        {
            // How a multi-tenant DMS is addressed. The API repeats the prefix in what it declares, so taking
            // the declared path whole and appending it to a connection URL that has it would ask for
            // "/tenant1/tenant1/data".
            var segment = ResolverFor(new Uri("https://server/tenant1/"))
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("dataManagementApi", "https://server/tenant1/data")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            segment.ShouldBe("data");
        }

        [Test]
        public void A_declaration_that_still_carries_a_route_placeholder_should_be_refused()
        {
            // A multi-tenant DMS asked for its paths at the server root answers with the placeholders, not
            // with values. Building requests from those would fail later and less clearly than this does.
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor()
                    .Resolve(
                        statedSegment: null,
                        // Spelled exactly as a multi-tenant DMS puts it in the document.
                        DiscoveryDeclaring(("dataManagementApi", "https://server/{tenant}/data")),
                        EdFiApiUrlSegmentResolver.DataManagement));

            exception.Message.ShouldContain("route placeholder");

            // Asserted against the escaped spelling the placeholder actually arrives in. The word "tenant" on
            // its own appears in the guidance appended to every refusal, so asserting that could not fail.
            exception.Message.ShouldContain("%7Btenant%7D");
        }

        [Test]
        public void A_segment_stated_on_the_connection_should_win_over_the_declaration()
        {
            var segment = ResolverFor()
                .Resolve(
                    statedSegment: "/gateway/data/",
                    DiscoveryDeclaring(("dataManagementApi", "https://server/data/v3/")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            segment.ShouldBe("gateway/data");
        }

        [Test]
        public void An_undeclared_change_queries_path_should_fall_back_and_say_so()
        {
            // Ordinary rather than exceptional: an ODS/API declares changeQueries only while the feature is
            // enabled, so this is the path a supported deployment takes, not only a broken one.
            using (TestCorrelator.CreateContext())
            {
                var segment = ResolverFor()
                    .Resolve(
                        statedSegment: null,
                        DiscoveryDeclaring(("dataManagementApi", "https://server/data/v3/")),
                        EdFiApiUrlSegmentResolver.ChangeQueries);

                segment.ShouldBe(EdFiApiConstants.ChangeQueriesApiSegment);

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .ShouldContain(e =>
                        e.Level == LogEventLevel.Information
                        && e.MessageTemplate.Text.Contains("did not declare"));
            }
        }

        [Test]
        public void A_document_that_declares_nothing_should_fall_back_to_the_conventional_path()
        {
            var segment = ResolverFor()
                .Resolve(
                    statedSegment: null,
                    DiscoveryDocument.Read(new JObject()),
                    EdFiApiUrlSegmentResolver.DataManagement);

            segment.ShouldBe(EdFiApiConstants.DataManagementApiSegment);
        }

        [Test]
        public void A_document_that_could_not_be_read_should_be_reported_differently_from_one_that_said_nothing()
        {
            // An ODS/API that declares no change queries because the feature is off is ordinary, and saying so
            // at Warning on every run teaches an operator to stop reading warnings. An API that could not be
            // asked at all is not ordinary, and the two are indistinguishable from the contents alone.
            using (TestCorrelator.CreateContext())
            {
                ResolverFor().Resolve(
                    statedSegment: null,
                    DiscoveryDocument.Read(new JObject()),
                    EdFiApiUrlSegmentResolver.ChangeQueries);

                var events = TestCorrelator.GetLogEventsFromCurrentContext().ToArray();

                events.ShouldContain(e =>
                    e.Level == LogEventLevel.Information
                    && e.MessageTemplate.Text.Contains("did not declare"));
                events.ShouldNotContain(e => e.Level == LogEventLevel.Warning);
            }

            using (TestCorrelator.CreateContext())
            {
                ResolverFor().Resolve(
                    statedSegment: null,
                    DiscoveryDocument.Unread,
                    EdFiApiUrlSegmentResolver.ChangeQueries);

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .ShouldContain(e =>
                        e.Level == LogEventLevel.Warning
                        && e.MessageTemplate.Text.Contains("could not be read"));
            }
        }

        [Test]
        public void A_path_declared_at_the_connection_URL_itself_should_keep_the_connection_prefix()
        {
            // An API that serves its resources at the same address as the connection. An empty segment would
            // make the composed resource path absolute, which resets to the server root and drops the prefix.
            var connection = new Uri("https://server/tenant1/");

            var segment = ResolverFor(connection)
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("dataManagementApi", "https://server/tenant1/")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            new Uri(connection, $"{segment}/ed-fi/students")
                .ShouldBe(new Uri("https://server/tenant1/ed-fi/students"));
        }

        [Test]
        public void A_declaration_carrying_a_query_string_should_be_refused()
        {
            // A relative URI keeps the query, and a resource path is appended rather than merged, so this
            // would address '/data?tenant=1/ed-fi/students': the resource inside the query string.
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor()
                    .Resolve(
                        statedSegment: null,
                        DiscoveryDeclaring(("dataManagementApi", "https://server/data?tenant=1")),
                        EdFiApiUrlSegmentResolver.DataManagement));

            exception.Message.ShouldContain("query string or a fragment");
        }

        [Test]
        public void A_declaration_carrying_a_fragment_should_be_refused()
        {
            // Worse than the query: everything after the '#' is never sent to the server at all.
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor()
                    .Resolve(
                        statedSegment: null,
                        DiscoveryDeclaring(("dataManagementApi", "https://server/data#section")),
                        EdFiApiUrlSegmentResolver.DataManagement));

            exception.Message.ShouldContain("query string or a fragment");
        }

        [Test]
        public void A_placeholder_that_does_not_name_the_school_year_should_still_be_refused()
        {
            // Answering any trailing placeholder with the school year turns a path that cannot be served into
            // one that looks valid and addresses the wrong route, and puts it past the refusal.
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor(schoolYear: 2024)
                    .Resolve(
                        statedSegment: null,
                        DiscoveryDeclaring(("dataManagementApi", "https://server/data/{tenant}")),
                        EdFiApiUrlSegmentResolver.DataManagement));

            exception.Message.ShouldContain("route placeholder");
        }

        [Test]
        public void A_segment_stated_on_the_connection_that_carries_a_route_placeholder_should_be_refused()
        {
            var exception = Should.Throw<InvalidConfigurationException>(
                () => ResolverFor()
                    .Resolve(
                        statedSegment: "{tenant}/data",
                        DiscoveryDeclaring(("dataManagementApi", "https://server/data")),
                        EdFiApiUrlSegmentResolver.DataManagement));

            exception.Message.ShouldContain("route placeholder");
        }

        [Test]
        public void A_declaration_beside_the_connection_path_should_resolve_back_to_it()
        {
            // Same host, but served alongside the connection's path rather than beneath it. Expressing it
            // relative to the connection yields a path that still composes onto the connection URL, where
            // taking the declared path as given would have prefixed the connection's own path to it.
            var segment = ResolverFor(new Uri("https://gateway/edfi/"))
                .Resolve(
                    statedSegment: null,
                    DiscoveryDeclaring(("dataManagementApi", "https://gateway/other/data/v3/")),
                    EdFiApiUrlSegmentResolver.DataManagement);

            new Uri(new Uri("https://gateway/edfi/"), $"{segment}/ed-fi/students")
                .ShouldBe(new Uri("https://gateway/other/data/v3/ed-fi/students"));
        }

        [Test]
        public void A_client_should_route_to_where_its_API_declares_it_serves()
        {
            // The end-to-end control for the ticket: a DMS-shaped Discovery document has to move the client
            // off the conventional path. Were resolution not reached at all, this would read "data/v3".
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .ApiVersionMetadataUrls(
                    apiVersion: "8.0",
                    edfiVersion: "5.2.0",
                    urls: new Dictionary<string, string>
                    {
                        // Neither value is the conventional one, so falling back rather than reading the
                        // declaration would show up here instead of matching by coincidence.
                        { "dataManagementApi", $"{MockRequests.SourceApiBaseUrl}/data" },
                        { "changeQueries", $"{MockRequests.SourceApiBaseUrl}/changes/" }
                    });

            using var client = CreateClient(fake);

            client.DataManagementApiSegment.ShouldBe("data");
            client.ChangeQueriesApiSegment.ShouldBe("changes");
        }

        [Test]
        public void A_client_whose_API_declares_nothing_should_keep_the_conventional_paths()
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .ApiVersionMetadataUrls(
                    apiVersion: "7.1",
                    edfiVersion: "5.0.0",
                    urls: new Dictionary<string, string>());

            using var client = CreateClient(fake);

            client.DataManagementApiSegment.ShouldBe(EdFiApiConstants.DataManagementApiSegment);
            client.ChangeQueriesApiSegment.ShouldBe(EdFiApiConstants.ChangeQueriesApiSegment);
        }

        [Test]
        public void A_school_year_should_be_applied_to_a_declared_path_exactly_once()
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .ApiVersionMetadataUrls(
                    apiVersion: "7.1",
                    edfiVersion: "5.0.0",
                    urls: new Dictionary<string, string>
                    {
                        // Declared without a year, and deliberately not the conventional path, so that the
                        // expectation cannot be met by the fallback appending the year.
                        { "dataManagementApi", $"{MockRequests.SourceApiBaseUrl}/data" }
                    });

            using var client = CreateClient(fake, schoolYear: 2099);

            client.DataManagementApiSegment.ShouldBe("data/2099");
        }

        [Test]
        public void A_school_year_already_present_in_a_declared_path_should_not_be_repeated()
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .ApiVersionMetadataUrls(
                    apiVersion: "7.1",
                    edfiVersion: "5.0.0",
                    urls: new Dictionary<string, string>
                    {
                        // Deliberately not the conventional path, so that falling back rather than reading
                        // the declaration would show up here instead of producing the same string by chance.
                        { "dataManagementApi", $"{MockRequests.SourceApiBaseUrl}/data/2099/" }
                    });

            using var client = CreateClient(fake, schoolYear: 2099);

            client.DataManagementApiSegment.ShouldBe("data/2099");
        }

        [Test]
        public void A_client_whose_discovery_document_is_not_found_should_keep_the_conventional_paths()
        {
            // The connection is reachable but its root is not, which is the deployment the per-connection
            // override exists for. Reading it has to degrade rather than end the run.
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler();
            A.CallTo(() => fake.Get($"{MockRequests.SourceApiBaseUrl}/", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.NotFound());

            using var client = CreateClient(fake);

            client.DataManagementApiSegment.ShouldBe(EdFiApiConstants.DataManagementApiSegment);
            client.ChangeQueriesApiSegment.ShouldBe(EdFiApiConstants.ChangeQueriesApiSegment);
        }

        [Test]
        public void A_client_whose_discovery_document_is_not_JSON_should_keep_the_conventional_paths()
        {
            // Something other than an Ed-Fi API answering at the connection URL, or a proxy error page. The
            // parse failure belongs to the document, not to the run.
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler();
            A.CallTo(() => fake.Get($"{MockRequests.SourceApiBaseUrl}/", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK("<html><body>Gateway Timeout</body></html>"));

            using var client = CreateClient(fake);

            client.DataManagementApiSegment.ShouldBe(EdFiApiConstants.DataManagementApiSegment);
        }

        private static EdFiApiVersionMetadataProviderBase VersionMetadataFor(EdFiApiClient client)
        {
            var provider = A.Fake<IEdFiApiClientProvider>();
            A.CallTo(() => provider.GetApiClient()).Returns(client);

            return new EdFiApiVersionMetadataProviderBase(client.Name, provider);
        }

        [Test]
        public void An_api_that_answered_without_a_discovery_document_should_be_reported_as_configuration()
        {
            // The exit code is what a scheduled job acts on. Whatever is at that address is not serving a
            // Discovery document, and a later run does not change that, so reporting an incomplete run
            // would invite the job to keep rerunning a configuration fault.
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler();
            A.CallTo(() => fake.Get($"{MockRequests.SourceApiBaseUrl}/", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.NotFound());

            using var client = CreateClient(fake);

            client.DiscoveryDocument.WasRead.ShouldBeFalse();
            client.DiscoveryDocument.Outcome.ShouldBe(DiscoveryOutcome.NotADocument);

            var exception = Should.Throw<InvalidConfigurationException>(
                () => VersionMetadataFor(client).GetVersionMetadata().GetAwaiter().GetResult());

            exception.Message.ShouldContain("not with a Discovery document");
            PublisherExitCode.ForFailure(exception).ShouldBe(PublisherExitCode.InvalidConfiguration);
        }

        [Test]
        public void An_api_answering_with_something_that_is_not_json_should_be_reported_as_configuration()
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler();
            A.CallTo(() => fake.Get($"{MockRequests.SourceApiBaseUrl}/", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK("<html><body>Gateway Timeout</body></html>"));

            using var client = CreateClient(fake);

            client.DiscoveryDocument.Outcome.ShouldBe(DiscoveryOutcome.NotADocument);

            Should.Throw<InvalidConfigurationException>(
                () => VersionMetadataFor(client).GetVersionMetadata().GetAwaiter().GetResult());
        }

        [TestCase(503)]
        [TestCase(502)]
        [TestCase(504)]
        [TestCase(429)]
        [TestCase(408)]
        public void A_status_a_later_run_could_get_past_should_stay_a_run_that_may_be_repeated(int statusCode)
        {
            // A gateway in front of an API that is restarting answers 503, and a saturated one answers 429.
            // Reporting those as configuration would have a scheduled job keep rerunning what it reads as a
            // fault it cannot fix, or stop rerunning what it could.
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler();
            A.CallTo(() => fake.Get($"{MockRequests.SourceApiBaseUrl}/", A<HttpRequestMessage>.Ignored))
                .Returns(new HttpResponseMessage((HttpStatusCode)statusCode));

            using var client = CreateClient(fake);

            client.DiscoveryDocument.Outcome.ShouldBe(DiscoveryOutcome.Unreachable);

            var exception = Should.Throw<Exception>(
                () => VersionMetadataFor(client).GetVersionMetadata().GetAwaiter().GetResult());

            exception.ShouldNotBeOfType<InvalidConfigurationException>();
            PublisherExitCode.ForFailure(exception).ShouldBe(PublisherExitCode.ProcessingIncomplete);
        }

        [TestCase(404)]
        [TestCase(401)]
        [TestCase(400)]
        public void A_status_that_will_not_change_should_be_reported_as_configuration(int statusCode)
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler();
            A.CallTo(() => fake.Get($"{MockRequests.SourceApiBaseUrl}/", A<HttpRequestMessage>.Ignored))
                .Returns(new HttpResponseMessage((HttpStatusCode)statusCode));

            using var client = CreateClient(fake);

            client.DiscoveryDocument.Outcome.ShouldBe(DiscoveryOutcome.NotADocument);

            Should.Throw<InvalidConfigurationException>(
                () => VersionMetadataFor(client).GetVersionMetadata().GetAwaiter().GetResult());
        }

        [Test]
        public void An_api_that_could_not_be_reached_should_stay_a_run_that_may_be_repeated()
        {
            // A restart or a transient network fault, which a later run may well resolve, so this one keeps
            // the exit code that says so rather than being called a configuration fault.
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler();
            A.CallTo(() => fake.Get($"{MockRequests.SourceApiBaseUrl}/", A<HttpRequestMessage>.Ignored))
                .Throws(new HttpRequestException("connection refused"));

            using var client = CreateClient(fake);

            client.DiscoveryDocument.WasRead.ShouldBeFalse();
            client.DiscoveryDocument.Outcome.ShouldBe(DiscoveryOutcome.Unreachable);

            var exception = Should.Throw<Exception>(
                () => VersionMetadataFor(client).GetVersionMetadata().GetAwaiter().GetResult());

            exception.ShouldNotBeOfType<InvalidConfigurationException>();
            exception.Message.ShouldContain("could not be reached");
            PublisherExitCode.ForFailure(exception).ShouldBe(PublisherExitCode.ProcessingIncomplete);
        }

        [Test]
        public void A_client_whose_discovery_document_carries_a_urls_value_that_is_not_an_object_should_keep_the_conventional_paths()
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler();
            A.CallTo(() => fake.Get($"{MockRequests.SourceApiBaseUrl}/", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK("{\"version\":\"8.0\",\"urls\":\"not-an-object\"}"));

            using var client = CreateClient(fake);

            client.DataManagementApiSegment.ShouldBe(EdFiApiConstants.DataManagementApiSegment);
        }

        private static EdFiApiClient CreateClient(IFakeHttpRequestHandler fake, int? schoolYear = null) =>
            new(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(schoolYear: schoolYear),
                bearerTokenRefreshMinutes: 27,
                ignoreSslErrors: true,
                httpClientHandler: new HttpClientHandlerFakeBridge(fake));
    }
}
