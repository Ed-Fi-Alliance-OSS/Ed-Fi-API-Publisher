// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Core.Metadata;
using EdFi.Tools.ApiPublisher.Core.Modules;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Configuration
{
    /// <summary>
    /// The run summary is assembled from counts taken in four different components, so it is only a summary
    /// of the run if they all write to the same collector. Nothing else would notice if that registration
    /// were lost: every count would land in a different instance and the printed table would simply be
    /// short, with the whole suite still green (see APIPUB-120).
    /// </summary>
    [TestFixture]
    public class CoreModuleRegistrationTests
    {
        [Test]
        public void The_run_summary_collector_is_shared_for_the_whole_run()
        {
            var builder = new ContainerBuilder();
            builder.RegisterModule<CoreModule>();

            using var container = builder.Build();

            var collector = container.Resolve<IRunSummaryCollector>();

            container.Resolve<IRunSummaryCollector>().ShouldBeSameAs(collector);
        }

        [Test]
        public void The_publishing_operation_metadata_collector_is_shared_for_the_whole_run()
        {
            // The run summary reads the source item counts this collector holds, so a second instance would
            // report every resource's expected count as unknown
            var builder = new ContainerBuilder();
            builder.RegisterModule<CoreModule>();

            using var container = builder.Build();

            var collector = container.Resolve<IPublishingOperationMetadataCollector>();

            container.Resolve<IPublishingOperationMetadataCollector>().ShouldBeSameAs(collector);
        }
    }
}
