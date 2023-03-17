// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;
using Newtonsoft.Json;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    /// <summary>
    /// Publishes errors without the original request content (due to security considerations) by logging
    /// the JSON serialized representations of the <see cref="ErrorItemMessage" />.
    /// </summary>
    public class SerilogErrorPublisher : IErrorPublisher
    {
        private readonly ILogger _logger = Log.Logger.ForContext(typeof(SerilogErrorPublisher));

        private long _publishedErrorCount;
        
        public Task PublishErrorsAsync(ErrorItemMessage[] messages)
        {
            return Task.Run(() =>
            {
                _logger.Error(JsonConvert.SerializeObject(messages, Formatting.Indented));
                Interlocked.Add(ref _publishedErrorCount, messages.Length);
            });
        }

        public long GetPublishedErrorCount()
        {
            return Interlocked.Read(ref _publishedErrorCount);
        }
    }
}
