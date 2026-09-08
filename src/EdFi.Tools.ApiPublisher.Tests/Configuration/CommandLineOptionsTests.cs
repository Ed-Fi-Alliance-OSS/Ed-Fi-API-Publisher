// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using System.IO;

namespace EdFi.Tools.ApiPublisher.Tests.Configuration
{
    /// <summary>
    /// An option is only usable if the command line reaches it: the switch mappings are a separate list from
    /// the options themselves, so an option added without a mapping binds to nothing and silently keeps its
    /// default (found end to end on APIPUB-120).
    /// </summary>
    [TestFixture]
    public class CommandLineOptionsTests
    {
        private readonly List<string> _createdSettingsFiles = new();

        [OneTimeSetUp]
        public void CreateRequiredSettingsFiles()
        {
            // The factory requires these files to be present in the working directory
            foreach (string fileName in new[] { "apiPublisherSettings.json", "configurationStoreSettings.json" })
            {
                if (File.Exists(fileName))
                {
                    continue;
                }

                // An empty section produces no configuration keys at all, so the file carries one unrelated
                // leaf value to make the options section bind
                File.WriteAllText(fileName, "{ \"options\": { \"includeDescriptors\": false } }");
                _createdSettingsFiles.Add(fileName);
            }
        }

        [OneTimeTearDown]
        public void RemoveCreatedSettingsFiles()
        {
            foreach (string fileName in _createdSettingsFiles)
            {
                File.Delete(fileName);
            }
        }

        [Test]
        public void The_tolerated_item_error_count_is_reachable_from_the_command_line()
        {
            var options = BuildOptions("--toleratedItemErrorCount=7");

            options.ToleratedItemErrorCount.ShouldBe(7);
        }

        [Test]
        public void The_tolerated_item_error_count_defaults_to_failing_on_any_lost_document()
        {
            var options = BuildOptions();

            options.ToleratedItemErrorCount.ShouldBe(0);
        }

        private static Options BuildOptions(params string[] commandLineArgs)
        {
            var configuration = new ConfigurationBuilderFactory().Create(commandLineArgs).Build();

            return configuration.Get<ApiPublisherSettings>().Options;
        }
    }
}
