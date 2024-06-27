// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Serilog.Events;
using Serilog.Formatting;
using System;
using System.Collections.Generic;
using System.IO;

namespace EdFi.Tools.ApiPublisher.Core.Configuration.Serilog;

public class TextFormatter : ITextFormatter
{
    private const string ThreadId = "ThreadId";
    private const string SourceContext = "SourceContext";
    private readonly string _format;
    public TextFormatter(string format = "[{0:yyyy-MM-dd HH:mm:ss,fff}] [{1}] [{2:00}] {3} - {4} {5} {6}")
    {
        _format = format;
    }
    public void Format(LogEvent logEvent, TextWriter output)
    {
        var threadId = GetValueFromProperty(logEvent.Properties.GetValueOrDefault(ThreadId));
        var sourceContext = GetValueFromProperty(logEvent.Properties.GetValueOrDefault(SourceContext));
        var message = logEvent.MessageTemplate.Render(logEvent.Properties);

        try
        {
            output.Write(_format, logEvent.Timestamp, GetShortFormatLevel(logEvent.Level), threadId, sourceContext, message, output.NewLine, logEvent.Exception?.Message, logEvent.Exception?.StackTrace);

        }
        catch (Exception ex)
        {
            output.Write($"Unable to render log message. Reason was {ex}");
        }
    }

    private string GetValueFromProperty(LogEventPropertyValue logEventPropertyValue)
    {
        string result = string.Empty;
        if (logEventPropertyValue is ScalarValue scalar && scalar.Value != null)
        {
            if (scalar.Value is string stringValue)
            {
                result = stringValue;
            }
            else
            {
                result = scalar.Value.ToString();
            }
        }
        return result;
    }

    private string GetShortFormatLevel(LogEventLevel logEventLevel)
    {
        var value = logEventLevel switch
        {
            LogEventLevel.Verbose => "ALL",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARN",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "FATAL",
            _ => throw new ArgumentException("Unexpected value for LogEvent.Level", nameof(logEventLevel))
        };

        return value.ToString();
    }
}
