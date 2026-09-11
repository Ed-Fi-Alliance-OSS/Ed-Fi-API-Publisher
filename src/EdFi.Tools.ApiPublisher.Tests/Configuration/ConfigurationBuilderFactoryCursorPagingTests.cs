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
    /// Verifies the command-line switches and the environment variable binding for the cursor paging options through
    /// the real configuration builder (settings files, then environment, then command line), including precedence.
    /// The settings files the builder requires come from Configuration/Fixtures, linked into the test output under
    /// the names the CLI ships them with.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class ConfigurationBuilderFactoryCursorPagingTests
    {
        private const string PartitionCountVariable = "EdFi__ApiPublisher__Options__CursorPagingPartitionCount";

        private static Options BindOptions(params string[] args)
            => new ConfigurationBuilderFactory().Create(args).Build().Get<ApiPublisherSettings>().Options;

        [TearDown]
        public void ClearEnvironment() => Environment.SetEnvironmentVariable(PartitionCountVariable, null);

        [Test]
        public void Defaults_should_leave_cursor_paging_enabled_with_no_explicit_partition_count()
        {
            var options = BindOptions();

            options.DisableCursorPaging.ShouldBeFalse();
            options.CursorPagingPartitionCount.ShouldBeNull();
        }

        [Test]
        public void Command_line_switches_should_bind_the_cursor_paging_options()
        {
            var options = BindOptions("--disableCursorPaging=true", "--cursorPagingPartitionCount=7");

            options.DisableCursorPaging.ShouldBeTrue();
            options.CursorPagingPartitionCount.ShouldBe(7);
            options.ResolvedCursorPagingPartitionCount.ShouldBe(7);
        }

        [Test]
        public void Environment_variable_should_bind_the_partition_count()
        {
            Environment.SetEnvironmentVariable(PartitionCountVariable, "8");

            BindOptions().CursorPagingPartitionCount.ShouldBe(8);
        }

        [Test]
        public void Command_line_should_take_precedence_over_the_environment()
        {
            Environment.SetEnvironmentVariable(PartitionCountVariable, "8");

            BindOptions("--cursorPagingPartitionCount=3").CursorPagingPartitionCount.ShouldBe(3);
        }

        [TestCase("0")]
        [TestCase("201")]
        public void Out_of_range_partition_count_from_the_command_line_should_be_rejected_on_resolution(string value)
        {
            var options = BindOptions($"--cursorPagingPartitionCount={value}");

            // Program.ValidateOptions reports the same range violation before processing starts
            Should.Throw<InvalidOperationException>(() => options.ResolvedCursorPagingPartitionCount);
        }
    }
}
