using System;
using System.Linq;
using log4net.Appender;
using log4net.Repository;

namespace EdFi.Tools.ApiPublisher.Tests.Extensions
{
    public static class LoggerRepositoryExtensions
    {
        public static string LoggedContent(this ILoggerRepository loggerRepository)
        {
            // Inspect the log entries
            var memoryAppender = loggerRepository.GetAppenders().OfType<MemoryAppender>().Single();
            var events = memoryAppender.GetEvents();

            return string.Join(Environment.NewLine, events.Select(e => e.RenderedMessage));
        }
    }
}