// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration.Serilog;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FluentAssertions;
using NUnit.Framework;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EdFi.Tools.ApiPublisher.Tests.Configuration.Serilog;

[TestFixture]
public class TextFormatterTests
{
    [TestFixture]
    public class When_use_the_TextFormatter_in_Serilog 
    {
        private const string Message = "My text format message";
        private const string LevelInfo = "[INFO]";

        private IEnumerable<LogEvent> _logEvents;

        public IEnumerable<LogEvent> LogEvents
        {
            get { return _logEvents; }
            set { _logEvents = value; }
        }

        [OneTimeSetUp]
        public void RunOnceBefore()
        {
            TestHelpers.InitializeLogging();
        }

        [OneTimeTearDown]
        public void RunOnceAfterAll()
        {
            Log.CloseAndFlush();
        }

        [TestCase()]
        public void Should_render_the_messages_with_default_format()
        {
            //Arrange
            var formatter = new TextFormatter();
            using (TestCorrelator.CreateContext())
            {
                Log.Information(Message);
                LogEvents = TestCorrelator.GetLogEventsFromCurrentContext();
            }
            LogEvents.Should().ContainSingle();
            var logEvent = LogEvents.FirstOrDefault();
            using (StringWriter textWriter = new StringWriter())
            {
                //Act
                formatter.Format(logEvent, textWriter);

                //Assert
                textWriter.ShouldNotBeNull();
                textWriter.ToString().ShouldContain(Message);
                textWriter.ToString().ShouldContain(LevelInfo);
                textWriter.ToString().ShouldContain(logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss,fff"));
            }
        }
        
        [TestCase("[{Level}] - {Message}")]
        public void Should_display_the_messages_with_custom_format(string format)
        {
            //Arrange
            var formatter = new TextFormatter(format);
            using (TestCorrelator.CreateContext())
            {
                Log.Information(Message);
                LogEvents = TestCorrelator.GetLogEventsFromCurrentContext();
            }
            LogEvents.Should().ContainSingle();
            var logEvent = LogEvents.FirstOrDefault();

            using (StringWriter textWriter = new StringWriter())
            {
                //Act
                formatter.Format(logEvent, textWriter);

                //Assert
                textWriter.ShouldNotBeNull();
                textWriter.ToString().ShouldBe(LevelInfo + " - " + Message);
            }

        }
    }
}
