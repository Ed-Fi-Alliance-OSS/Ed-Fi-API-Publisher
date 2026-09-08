// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System.Linq;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies the per-resource paging strategy decision (APIPUB-139): offset when disabled or when the offset
    /// workarounds (change-version / reverse paging) are in use, offset for deletes and key changes, otherwise
    /// cursor iff the source supports it; memoized per resource with one log line each.
    /// </summary>
    [TestFixture]
    public class SourcePagingStrategyResolverTests
    {
        private static (SourcePagingStrategyResolver resolver, ISourceCapabilities capabilities) Create(bool supportsCursorPaging)
        {
            var capabilities = A.Fake<ISourceCapabilities>();
            A.CallTo(() => capabilities.SupportsCursorPagingAsync(A<string>.Ignored)).Returns(supportsCursorPaging);

            return (new SourcePagingStrategyResolver(capabilities), capabilities);
        }

        [Test]
        public async Task Supported_source_should_resolve_to_cursor_paging()
        {
            var (resolver, _) = Create(supportsCursorPaging: true);

            (await resolver.ResolveAsync("/ed-fi/students", TestHelpers.GetOptions())).ShouldBe(SourcePagingStrategy.Cursor);
        }

        [Test]
        public async Task Unsupported_source_should_resolve_to_offset_paging()
        {
            var (resolver, _) = Create(supportsCursorPaging: false);

            (await resolver.ResolveAsync("/ed-fi/students", TestHelpers.GetOptions())).ShouldBe(SourcePagingStrategy.Offset);
        }

        [Test]
        public async Task DisableCursorPaging_should_force_offset_paging_without_probing()
        {
            var (resolver, capabilities) = Create(supportsCursorPaging: true);
            var options = TestHelpers.GetOptions();
            options.DisableCursorPaging = true;

            (await resolver.ResolveAsync("/ed-fi/students", options)).ShouldBe(SourcePagingStrategy.Offset);
            A.CallTo(() => capabilities.SupportsCursorPagingAsync(A<string>.Ignored)).MustNotHaveHappened();
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task Offset_workarounds_should_keep_offset_paging(bool useChangeVersionPaging, bool useReversePaging)
        {
            var (resolver, capabilities) = Create(supportsCursorPaging: true);
            var options = TestHelpers.GetOptions();
            options.UseChangeVersionPaging = useChangeVersionPaging;
            options.UseReversePaging = useReversePaging;

            (await resolver.ResolveAsync("/ed-fi/students", options)).ShouldBe(SourcePagingStrategy.Offset);
            A.CallTo(() => capabilities.SupportsCursorPagingAsync(A<string>.Ignored)).MustNotHaveHappened();
        }

        [TestCase("/ed-fi/students/deletes")]
        [TestCase("/ed-fi/students/keyChanges")]
        public async Task Deletes_and_key_changes_should_always_use_offset_paging(string resourceUrl)
        {
            var (resolver, capabilities) = Create(supportsCursorPaging: true);

            (await resolver.ResolveAsync(resourceUrl, TestHelpers.GetOptions())).ShouldBe(SourcePagingStrategy.Offset);
            A.CallTo(() => capabilities.SupportsCursorPagingAsync(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public async Task Resolution_should_be_memoized_per_resource_and_logged_once()
        {
            TestHelpers.InitializeLogging();
            var (resolver, capabilities) = Create(supportsCursorPaging: true);
            var options = TestHelpers.GetOptions();

            using (TestCorrelator.CreateContext())
            {
                await resolver.ResolveAsync("/ed-fi/students", options);
                await resolver.ResolveAsync("/ed-fi/students", options);
                await resolver.ResolveAsync("/ed-fi/schools", options);

                A.CallTo(() => capabilities.SupportsCursorPagingAsync("/ed-fi/students")).MustHaveHappenedOnceExactly();
                A.CallTo(() => capabilities.SupportsCursorPagingAsync("/ed-fi/schools")).MustHaveHappenedOnceExactly();

                var strategyLines = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Where(e => e.MessageTemplate.Text == "{ResourceUrl}: using {Strategy} paging")
                    .ToArray();

                strategyLines.Length.ShouldBe(2);
                strategyLines.Single(e => e.RenderMessage().StartsWith("\"/ed-fi/students\"")).RenderMessage()
                    .ShouldBe("\"/ed-fi/students\": using Cursor paging");
                strategyLines.All(e => e.Level == LogEventLevel.Information).ShouldBeTrue();
            }
        }
    }
}
