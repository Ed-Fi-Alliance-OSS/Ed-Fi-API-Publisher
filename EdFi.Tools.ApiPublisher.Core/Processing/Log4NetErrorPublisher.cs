using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    /// <summary>
    /// Publishes errors without the original request content (due to security considerations) by logging
    /// the JSON serialized representations of the <see cref="ErrorItemMessage" />.
    /// </summary>
    public class Log4NetErrorPublisher : IErrorPublisher
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(Log4NetErrorPublisher));

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