// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Isolation;
using EdFi.Tools.ApiPublisher.Core.Metadata;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Versioning;

namespace EdFi.Tools.ApiPublisher.Core.Modules
{
    public class CoreModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<ResourceDependencyProvider>()
                .As<IResourceDependencyProvider>()
                .SingleInstance();

            builder.RegisterType<SerilogErrorPublisher>()
                .As<IErrorPublisher>()
                .SingleInstance();

            // General purpose version checker
            builder.RegisterType<EdFiVersionsChecker>()
                .As<IEdFiVersionsChecker>()
                .SingleInstance();

            // Register decorators for collecting publishing operation metadata
            builder.RegisterType<PublishingOperationMetadataCollector>()
                .As<IPublishingOperationMetadataCollector>()
                .SingleInstance();

            builder.RegisterDecorator<CurrentChangeVersionCollector, ISourceCurrentChangeVersionProvider>();
            builder.RegisterDecorator<ResourceItemCountCollector, ISourceTotalCountProvider>();

            builder.RegisterDecorator<SourceEdFiVersionMetadataCollector, ISourceEdFiApiVersionMetadataProvider>();
            builder.RegisterDecorator<TargetEdFiVersionMetadataCollector, ITargetEdFiApiVersionMetadataProvider>();

            // Block factories
            builder.RegisterType<StreamResourceBlockFactory>(); //.SingleInstance();
            builder.RegisterType<StreamResourcePagesBlockFactory>(); //.SingleInstance();
            builder.RegisterType<PublishErrorsBlocksFactory>(); //.SingleInstance();

            builder.RegisterType<ChangeProcessor>();

            // Register fallback implementations
            builder.RegisterType<FallbackGraphMLDependencyMetadataProvider>()
                .As<IGraphMLDependencyMetadataProvider>();

            builder.RegisterType<FallbackTargetEdFiApiVersionMetadataProvider>().As<ITargetEdFiApiVersionMetadataProvider>();
            builder.RegisterType<FallbackSourceEdFiApiVersionMetadataProvider>().As<ISourceEdFiApiVersionMetadataProvider>();

            builder.RegisterType<FallbackSourceIsolationApplicator>().As<ISourceIsolationApplicator>().SingleInstance();
        }
    }
}
