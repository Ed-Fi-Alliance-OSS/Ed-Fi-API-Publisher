using Serilog;
using Serilog.Events;
using System.Threading;

namespace EdFi.Tools.ApiPublisher.Core.Helpers
{
    public static class ExponentialBackOffHelper
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(ExponentialBackOffHelper));
        
        public static void PerformDelay(ref int delay)
        {
            if (_logger.IsEnabled(LogEventLevel.Debug))
                _logger.Debug($"Performing exponential \"back off\" of thread for {delay} milliseconds.");
            
            Thread.Sleep(delay);
            
            delay = delay * 2;
        }
    }
}