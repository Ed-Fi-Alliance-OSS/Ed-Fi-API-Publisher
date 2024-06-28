// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration.Serilog;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FluentAssertions;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using NUnit.Framework;
using NUnit.Framework.Internal;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using ILogger = Serilog.ILogger;

namespace EdFi.Tools.ApiPublisher.Tests.Configuration.Serilog;

[TestFixture]
public class TextFormatterTests
{
    [TestFixture]
    public class When_use_the_TextFormatter_in_Serilog 
    {
        private const string Message = "My text format message";
        private const string LevelInfoPlain = "INFO";
        private const string LevelErrorPlain = "ERROR";
        private const string ThreadIdPropertyName = "ThreadId";
        private const string LevelInfoFormatted = $"[{LevelInfoPlain}]";
        private const int ThreadId = 10;
        private readonly Type _contextType = typeof(When_use_the_TextFormatter_in_Serilog);
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
            var logEvent = CreateSerilogEntry();

            using (StringWriter textWriter = new StringWriter())
            {
                //Act
                formatter.Format(logEvent, textWriter);

                //Assert
                textWriter.ShouldNotBeNull();
                textWriter.ToString().ShouldContain(Message);
                textWriter.ToString().ShouldContain(LevelInfoFormatted);
                textWriter.ToString().ShouldContain(logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss,fff"));
            }
        }
        
        [TestCase("[{Level}] - {Message}")]
        public void Should_display_the_messages_with_custom_format(string format)
        {
            //Arrange
            var formatter = new TextFormatter(format);
            var logEvent = CreateSerilogEntry();

            using (StringWriter textWriter = new StringWriter())
            {
                //Act
                formatter.Format(logEvent, textWriter);

                //Assert
                textWriter.ShouldNotBeNull();
                textWriter.ToString().ShouldBe(LevelInfoFormatted + " - " + Message);
            }
        }

        [TestCase()]
        public void Should_convert_EventLog_to_LogEventFormatValues_when_is_Info()
        {
            //Arrange
            var logEvent = CreateSerilogEntry();

            //Act
            var lEventFormatValues = new LogEventFormatValues(logEvent);

            //Assert
            lEventFormatValues.ShouldNotBeNull();
            lEventFormatValues.Message.ShouldContain(Message);
            lEventFormatValues.Level.ShouldContain(LevelInfoPlain);
            lEventFormatValues.SourceContext.ShouldBe(_contextType.FullName);
            lEventFormatValues.ThreadId.ShouldBe(ThreadId);
            lEventFormatValues.Timestamp.ShouldBe(logEvent.Timestamp.DateTime);

        }

        [TestCase()]
        public void Should_convert_EventLog_to_LogEventFormatValues_when_is_Error()
        {
            //Arrange
            var exception = new Exception(Message);
            var logEvent = CreateSerilogEntry(LogEventLevel.Error, exception);

            //Act
            var lEventFormatValues = new LogEventFormatValues(logEvent);

            //Assert
            lEventFormatValues.ShouldNotBeNull();
            lEventFormatValues.Exception.ShouldContain(Message);
            lEventFormatValues.Message.ShouldBeEmpty();
            lEventFormatValues.Level.ShouldContain(LevelErrorPlain);
            lEventFormatValues.SourceContext.ShouldBe(_contextType.FullName);
            lEventFormatValues.ThreadId.ShouldBe(ThreadId);
            lEventFormatValues.Timestamp.ShouldBe(logEvent.Timestamp.DateTime);
        }

        private LogEvent CreateSerilogEntry(LogEventLevel logEventLevel = LogEventLevel.Information, Exception exception = null)
        {
            using (TestCorrelator.CreateContext())
            {
                ILogger logger = Log.Logger.ForContext(_contextType);
                if (logEventLevel == LogEventLevel.Information)
                {
                    logger.Information(Message);

                }
                else if (logEventLevel == LogEventLevel.Error)
                {
                    logger.Error(exception, "");

                }
                LogEvents = TestCorrelator.GetLogEventsFromCurrentContext();
            }
            LogEvents.Should().ContainSingle();
            var logEvent = LogEvents.FirstOrDefault();
            var propertyThreadId = new LogEventProperty(ThreadIdPropertyName, new ScalarValue(ThreadId));
            logEvent.AddPropertyIfAbsent(propertyThreadId);

            return logEvent;
        }
    }

   
}
