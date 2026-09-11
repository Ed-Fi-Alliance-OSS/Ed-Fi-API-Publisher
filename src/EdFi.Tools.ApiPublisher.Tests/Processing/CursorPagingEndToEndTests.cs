// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Acceptance checks for APIPUB-139 through the full ChangeProcessor pipeline: a source that serves
    /// /partitions is read with partition and pageToken requests only (every item published once, "using Cursor
    /// paging" logged), and a source without /partitions -- or --disableCursorPaging -- produces exactly today's
    /// offset/limit requests. Cursor paging detection is a single /partitions probe with no API version gate,
    /// so the version advertised by the fake matters only to the snapshot endpoint the pipeline calls.
    /// </summary>
    [TestFixture]
    public class CursorPagingEndToEndTests
    {
        private const string Resource = "/ed-fi/stateEducationAgencies";
        private const int PageSize = 25;
        private const int PagesPerPartition = 2;
        private const int Partitions = 2;
        private const int TotalItems = Partitions * PagesPerPartition * PageSize;

        private static (IFakeHttpRequestHandler source, ConcurrentBag<HttpRequestMessage> resourceRequests) CreateSource(
            string apiVersion,
            bool partitionsSupported)
        {
            var resourceFaker = TestHelpers.GetGenericResourceFaker();
            var resourceRequests = new ConcurrentBag<HttpRequestMessage>();

            var source = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .ApiVersionMetadata(apiVersion: apiVersion)
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: TotalItems);

            // Partitions (detection probe and producer): tokens are "p{partition}-{page}". A source without the
            // 7.3 endpoint answers 404, because the path collides with the {id:guid} route.
            A.CallTo(
                    () => source.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath.EndsWith($"{Resource}/partitions"))))
                .ReturnsLazily(
                    () => partitionsSupported
                        ? FakeResponse.OK(new { pageTokens = Enumerable.Range(1, Partitions).Select(p => $"p{p}-1").ToArray() })
                        : FakeResponse.NotFound());

            // Pages: cursor requests are served by token, offset requests by offset
            A.CallTo(
                    () => source.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(
                            msg => msg.RequestUri.LocalPath.EndsWith(Resource)
                                && msg.RequestUri.ParseQueryString()["totalCount"] != "true")))
                .ReturnsLazily(
                    call =>
                    {
                        var request = (HttpRequestMessage)call.Arguments[1];
                        resourceRequests.Add(request);
                        var query = request.RequestUri.ParseQueryString();

                        if (query["pageToken"] is string token)
                        {
                            var parts = token.Substring(1).Split('-').Select(int.Parse).ToArray();
                            int partition = parts[0], page = parts[1];

                            // The page past the end of the partition is empty and carries no Next-Page-Token,
                            // which is what ends the partition walk
                            if (page > PagesPerPartition)
                            {
                                return FakeResponse.OK("[]");
                            }

                            var items = resourceFaker.Generate(PageSize);

                            for (int i = 0; i < items.Count; i++)
                            {
                                items[i].VehicleManufacturer = $"item-{(((partition - 1) * PagesPerPartition) + (page - 1)) * PageSize + i}";
                            }

                            return FakeResponse.OK(items).AppendHeaders(("Next-Page-Token", $"p{partition}-{page + 1}"));
                        }

                        long offset = long.Parse(query["offset"] ?? "0");

                        if (offset >= TotalItems)
                        {
                            return FakeResponse.OK("[]");
                        }

                        var offsetItems = resourceFaker.Generate(PageSize);

                        for (int i = 0; i < offsetItems.Count; i++)
                        {
                            offsetItems[i].VehicleManufacturer = $"item-{offset + i}";
                        }

                        return FakeResponse.OK(offsetItems);
                    });

            return (source, resourceRequests);
        }

        private static async Task<ConcurrentBag<string>> RunAsync(IFakeHttpRequestHandler source, bool disableCursorPaging = false)
        {
            var postedStamps = new ConcurrentBag<string>();
            var target = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            A.CallTo(() => target.Post(A<string>.That.Matches(url => url.EndsWith(Resource)), A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    call =>
                    {
                        var request = (HttpRequestMessage)call.Arguments[1];
                        string body = request.Content!.ReadAsStringAsync().Result;
                        postedStamps.Add(JObject.Parse(body)["vehicleManufacturer"]!.Value<string>()!);

                        return new HttpResponseMessage(HttpStatusCode.OK);
                    });

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;
            options.StreamingPageSize = PageSize;
            options.MaxDegreeOfParallelismForStreamResourcePages = Partitions;
            options.DisableCursorPaging = disableCursorPaging;

            var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                TestHelpers.GetSourceApiConnectionDetails(include: new[] { Resource }),
                source,
                TestHelpers.GetTargetApiConnectionDetails(),
                target);

            await changeProcessor.ProcessChangesAsync(TestHelpers.CreateChangeProcessorConfiguration(options), CancellationToken.None);

            return postedStamps;
        }

        private static void ShouldHavePublishedEveryItemOnce(ConcurrentBag<string> postedStamps)
        {
            postedStamps.Count.ShouldBe(TotalItems);
            postedStamps.ToHashSet().SetEquals(Enumerable.Range(0, TotalItems).Select(i => $"item-{i}")).ShouldBeTrue();
        }

        [Test]
        public async Task A_source_with_partitions_should_be_read_with_partitions_and_page_tokens_only()
        {
            TestHelpers.InitializeLogging();

            var (source, requests) = CreateSource("7.3", partitionsSupported: true);

            using (TestCorrelator.CreateContext())
            {
                var posted = await RunAsync(source);

                ShouldHavePublishedEveryItemOnce(posted);
                requests.ShouldNotBeEmpty();
                requests.All(r => r.HasParameter("pageToken") && r.HasParameter("pageSize")).ShouldBeTrue();
                requests.Any(r => r.HasParameter("offset") || r.HasParameter("limit")).ShouldBeFalse();

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .Count(e => e.MessageTemplate.Text == "{ResourceUrl}: using {Strategy} paging" && e.RenderMessage().EndsWith("using Cursor paging"))
                    .ShouldBe(1);
            }
        }

        [Test]
        public async Task A_source_without_partitions_should_be_read_with_offset_and_limit_exactly_as_before()
        {
            TestHelpers.InitializeLogging();

            var (source, requests) = CreateSource("5.2", partitionsSupported: false);

            var posted = await RunAsync(source);

            ShouldHavePublishedEveryItemOnce(posted);
            requests.All(r => r.HasParameter("offset") && r.HasParameter("limit") && !r.HasParameter("pageToken")).ShouldBeTrue();

            // Detection has no API version gate: the source is probed once for the whole run, and the single
            // 404 is what settles every resource onto offset/limit paging
            A.CallTo(
                    () => source.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath.EndsWith("/partitions"))))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task DisableCursorPaging_should_force_offset_and_limit_on_a_source_with_partitions()
        {
            TestHelpers.InitializeLogging();

            var (source, requests) = CreateSource("7.3", partitionsSupported: true);

            var posted = await RunAsync(source, disableCursorPaging: true);

            ShouldHavePublishedEveryItemOnce(posted);
            requests.All(r => r.HasParameter("offset") && !r.HasParameter("pageToken")).ShouldBeTrue();

            A.CallTo(
                    () => source.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath.EndsWith("/partitions"))))
                .MustNotHaveHappened();
        }
    }
}
