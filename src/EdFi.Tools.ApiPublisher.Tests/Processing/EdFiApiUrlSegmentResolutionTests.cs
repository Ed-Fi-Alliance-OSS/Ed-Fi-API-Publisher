// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;

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
    public class EdFiApiUrlSegmentResolutionTests
    {
        private static readonly Uri ServerRoot = new("https://server/");

        private static JObject DiscoveryDeclaring(params (string Name, string Url)[] urls)
        {
            var declared = new JObject();

            foreach (var (name, url) in urls)
            {
                declared[name] = url;
            }

            return new JObject { ["urls"] = declared };
        }

        private static EdFiApiUrlSegmentResolver ResolverFor(Uri baseAddress = null, int? schoolYear = null) =>
            new(baseAddress ?? ServerRoot, "TestSource", schoolYear);

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
        public void A_declaration_on_another_host_should_be_refused()
        {
            // The Discovery document is served by the remote API, and a connection's requests carry its
            // credentials. Following a declaration off the connection's own host would send this API's bearer
            // token, and the documents being published, somewhere the operator never named.
            var exception = Should.Throw<InvalidOperationException>(
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
            var exception = Should.Throw<InvalidOperationException>(
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
            var exception = Should.Throw<InvalidOperationException>(
                () => ResolverFor()
                    .Resolve(
                        statedSegment: null,
                        // Spelled exactly as a multi-tenant DMS puts it in the document.
                        DiscoveryDeclaring(("dataManagementApi", "https://server/{tenant}/data")),
                        EdFiApiUrlSegmentResolver.DataManagement));

            exception.Message.ShouldContain("route placeholder");
            exception.Message.ShouldContain("tenant");
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
            TestHelpers.InitializeLogging();

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
                        e.Level == LogEventLevel.Warning
                        && e.MessageTemplate.Text.Contains("did not declare"));
            }
        }

        [Test]
        public void An_unreadable_discovery_document_should_fall_back_to_the_conventional_path()
        {
            var segment = ResolverFor()
                .Resolve(statedSegment: null, new JObject(), EdFiApiUrlSegmentResolver.DataManagement);

            segment.ShouldBe(EdFiApiConstants.DataManagementApiSegment);
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
                        { "dataManagementApi", $"{MockRequests.SourceApiBaseUrl}/data" },
                        { "changeQueries", $"{MockRequests.SourceApiBaseUrl}/changeQueries/v1/" }
                    });

            using var client = CreateClient(fake);

            client.DataManagementApiSegment.ShouldBe("data");
            client.ChangeQueriesApiSegment.ShouldBe("changeQueries/v1");
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
                        // Declared without the year, which is what a year-specific ODS/API states.
                        { "dataManagementApi", $"{MockRequests.SourceApiBaseUrl}/data/v3/" }
                    });

            using var client = CreateClient(fake, schoolYear: 2099);

            client.DataManagementApiSegment.ShouldBe("data/v3/2099");
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

        private static EdFiApiClient CreateClient(IFakeHttpRequestHandler fake, int? schoolYear = null) =>
            new(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(schoolYear: schoolYear),
                bearerTokenRefreshMinutes: 27,
                ignoreSslErrors: true,
                httpClientHandler: new HttpClientHandlerFakeBridge(fake));
    }
}
