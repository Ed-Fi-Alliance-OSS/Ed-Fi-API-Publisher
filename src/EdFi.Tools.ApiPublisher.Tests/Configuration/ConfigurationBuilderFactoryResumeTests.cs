// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;
using System;

namespace EdFi.Tools.ApiPublisher.Tests.Configuration
{
    /// <summary>
    /// Verifies the command-line switches and the environment variable binding for the resume options
    /// (APIPUB-142) through the real configuration builder. Without a switch mapping a CLI flag binds to
    /// nothing and does so silently, which a test that sets <see cref="Options" /> directly never notices.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class ConfigurationBuilderFactoryResumeTests
    {
        private const string ResumeVariable = "EdFi__ApiPublisher__Options__ResumeLastRun";
        private const string RunStatePathVariable = "EdFi__ApiPublisher__Options__RunStatePath";

        private static Options BindOptions(params string[] args)
            => new ConfigurationBuilderFactory().Create(args).Build().Get<ApiPublisherSettings>().Options;

        [TearDown]
        public void ClearEnvironment()
        {
            Environment.SetEnvironmentVariable(ResumeVariable, null);
            Environment.SetEnvironmentVariable(RunStatePathVariable, null);
        }

        [Test]
        public void Defaults_should_not_resume_and_should_leave_the_state_path_unset()
        {
            var options = BindOptions();

            options.ResumeLastRun.ShouldBeFalse();
            options.RunStatePath.ShouldBeNull();
        }

        [Test]
        public void Command_line_switches_should_bind_the_resume_options()
        {
            var options = BindOptions("--resumeLastRun=true", "--runStatePath=/state/run.json");

            options.ResumeLastRun.ShouldBeTrue();
            options.RunStatePath.ShouldBe("/state/run.json");
        }

        [Test]
        public void Environment_variables_should_bind_the_resume_options()
        {
            Environment.SetEnvironmentVariable(ResumeVariable, "true");
            Environment.SetEnvironmentVariable(RunStatePathVariable, "/mounted/state");

            var options = BindOptions();

            options.ResumeLastRun.ShouldBeTrue();
            options.RunStatePath.ShouldBe("/mounted/state");
        }

        [Test]
        public void A_command_line_switch_should_win_over_the_environment_variable()
        {
            Environment.SetEnvironmentVariable(ResumeVariable, "false");
            Environment.SetEnvironmentVariable(RunStatePathVariable, "/from/environment");

            var options = BindOptions("--resumeLastRun=true", "--runStatePath=/from/command/line");

            options.ResumeLastRun.ShouldBeTrue();
            options.RunStatePath.ShouldBe("/from/command/line");
        }
    }
}
