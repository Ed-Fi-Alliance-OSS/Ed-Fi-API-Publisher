// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Serilog.Events;
using Serilog.Formatting;
using SmartFormat;
using SmartFormat.Core.Settings;
using System;
using System.Collections.Generic;
using System.IO;

namespace EdFi.Tools.ApiPublisher.Core.Configuration.Serilog;

public class TextFormatter : ITextFormatter
{
    private readonly string _format;
    public TextFormatter(string format = "[{Timestamp:yyyy-MM-dd HH:mm:ss,fff}] [{Level}] [{ThreadId:00}] {SourceContext} - {Message} {Exception} {NewLine}")
    {
        _format = format;
    }
    public void Format(LogEvent logEvent, TextWriter output)
    {
        try
        {
            var sf = Smart.CreateDefaultSmartFormat(new SmartSettings
            {
                StringFormatCompatibility = true
            });
            var logEventFormatValues = new LogEventFormatValues(logEvent);
            var formatted = sf.Format(_format, logEventFormatValues);
            output.Write(formatted);
        }
        catch (Exception ex)
        {
            output.Write($"Unable to render log message. Reason was {ex}");
        }
    }
}

public class LogEventFormatValues
{
    private const string ThreadIdSerilogPropertyName = "ThreadId";
    private const string SourceContextSerilogPropertyName = "SourceContext";
    
    public DateTime Timestamp => _logEvent.Timestamp.DateTime;
    public string Level => GetShortFormatLevel(_logEvent.Level);
    public string SourceContext => GetValueFromProperty(_logEvent.Properties.GetValueOrDefault(SourceContextSerilogPropertyName));
    public string Message => _logEvent.MessageTemplate.Render(_logEvent.Properties);
    public string Exception => $"{_logEvent.Exception?.Message} {_logEvent.Exception?.StackTrace}";
    public string ThreadId => GetValueFromProperty(_logEvent.Properties.GetValueOrDefault(ThreadIdSerilogPropertyName));

    public string NewLine => Environment.NewLine;

    private readonly LogEvent _logEvent;

    public LogEventFormatValues(LogEvent logEvent)
    {
        _logEvent = logEvent;
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

}
