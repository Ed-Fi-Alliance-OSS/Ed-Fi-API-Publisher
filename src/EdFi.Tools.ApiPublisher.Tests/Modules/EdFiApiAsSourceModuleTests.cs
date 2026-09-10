// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Api.Modules;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Capabilities;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageHandlers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;

namespace EdFi.Tools.ApiPublisher.Tests.Modules
{
    /// <summary>
    /// Builds the production Autofac graph for an Ed-Fi API source and verifies that the cursor paging
    /// registrations (APIPUB-139) actually resolve -- the dispatching producer, the page-request strategy
    /// dispatcher, the paging strategy resolver and the source capabilities. Resolution alone must not
    /// perform any HTTP: the source API client is registered as a Lazy instance.
    /// </summary>
    [TestFixture]
    public class EdFiApiAsSourceModuleTests
    {
        private const string OffsetPagingProducerKey = "OffsetPaging";

        private static IContainer BuildContainer(params (string key, string value)[] additionalSettings)
        {
            var settings = new Dictionary<string, string>
            {
                ["Connections:Source:Url"] = "https://test-source.example.com/",
                ["Connections:Source:Key"] = "sourceKey",
                ["Connections:Source:Secret"] = "sourceSecret",
                ["Options:StreamingPageSize"] = "500",
            };

            foreach (var (key, value) in additionalSettings)
            {
                settings[key] = value;
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();

            var builder = new ContainerBuilder();
            builder.RegisterModule(new EdFiApiAsSourceModule(configuration));

            return builder.Build();
        }

        [Test]
        public void Source_module_should_resolve_the_cursor_paging_graph()
        {
            using var container = BuildContainer();

            container.Resolve<IStreamResourcePageMessageProducer>()
                .ShouldBeOfType<PagingStrategyDispatchingStreamResourcePageMessageProducer>();

            container.Resolve<IPageRequestStrategy>()
                .ShouldBeOfType<PageRequestStrategyDispatcher>();

            container.Resolve<IStreamResourcePageMessageHandler>()
                .ShouldBeOfType<EdFiApiStreamResourcePageMessageHandler>();

            container.Resolve<ISourcePagingStrategyResolver>()
                .ShouldBeOfType<SourcePagingStrategyResolver>();

            container.Resolve<ISourceCapabilities>()
                .ShouldBeOfType<EdFiApiSourceCapabilities>();
        }

        [Test]
        public void Source_module_should_resolve_with_reverse_change_version_paging_configured()
        {
            using var container = BuildContainer(
                ("Options:UseChangeVersionPaging", "true"),
                ("Options:UseReversePaging", "true"));

            container.Resolve<IStreamResourcePageMessageProducer>()
                .ShouldBeOfType<PagingStrategyDispatchingStreamResourcePageMessageProducer>();

            container.ResolveNamed<IStreamResourcePageMessageProducer>(OffsetPagingProducerKey)
                .ShouldBeOfType<EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer>();
        }
    }
}
