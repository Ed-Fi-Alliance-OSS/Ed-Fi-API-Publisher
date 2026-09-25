// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Configuration
{
    /// <summary>
    /// Verifies the command-line switches for the per-connection path settings (APIPUB-108) through the real
    /// configuration builder. Without a switch mapping a CLI flag binds to nothing and does so silently, which
    /// matters more here than elsewhere: these settings are the remedy for an API whose Discovery document
    /// cannot be read, so a flag that quietly does nothing leaves that deployment with no way out at all.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class ConfigurationBuilderFactoryUrlSegmentTests
    {
        private static IConfigurationRoot Build(params string[] args) =>
            new ConfigurationBuilderFactory().Create(args).Build();

        [Test]
        public void Command_line_switches_should_bind_the_source_paths()
        {
            var configuration = Build(
                "--sourceDataManagementUrlSegment=data",
                "--sourceChangeQueriesUrlSegment=changes");

            configuration["Connections:Source:DataManagementUrlSegment"].ShouldBe("data");
            configuration["Connections:Source:ChangeQueriesUrlSegment"].ShouldBe("changes");
        }

        [Test]
        public void Command_line_switches_should_bind_the_target_paths()
        {
            var configuration = Build(
                "--targetDataManagementUrlSegment=gateway/data",
                "--targetChangeQueriesUrlSegment=gateway/changes");

            configuration["Connections:Target:DataManagementUrlSegment"].ShouldBe("gateway/data");
            configuration["Connections:Target:ChangeQueriesUrlSegment"].ShouldBe("gateway/changes");
        }

        [Test]
        public void A_source_switch_should_not_bind_the_target_path()
        {
            // The four switches differ only by role and by which path they name, so a transposed mapping would
            // still bind something and still look configured.
            var configuration = Build("--sourceDataManagementUrlSegment=data");

            configuration["Connections:Source:DataManagementUrlSegment"].ShouldBe("data");
            configuration["Connections:Target:DataManagementUrlSegment"].ShouldBeNull();
            configuration["Connections:Source:ChangeQueriesUrlSegment"].ShouldBeNull();
        }

        [Test]
        public void Defaults_should_leave_every_path_unset()
        {
            var configuration = Build();

            configuration["Connections:Source:DataManagementUrlSegment"].ShouldBeNull();
            configuration["Connections:Source:ChangeQueriesUrlSegment"].ShouldBeNull();
            configuration["Connections:Target:DataManagementUrlSegment"].ShouldBeNull();
            configuration["Connections:Target:ChangeQueriesUrlSegment"].ShouldBeNull();
        }
    }
}
