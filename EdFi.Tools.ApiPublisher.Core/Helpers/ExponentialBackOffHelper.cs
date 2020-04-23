using System.Threading;
using log4net;

namespace EdFi.Tools.ApiPublisher.Core.Helpers
{
    public static class ExponentialBackOffHelper
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(ExponentialBackOffHelper));
        
        public static void PerformDelay(ref int delay)
        {
            if (_logger.IsDebugEnabled)
                _logger.Debug($"Performing exponential \"back off\" of thread for {delay} milliseconds.");
            
            Thread.Sleep(delay);
            
            delay = delay * 2;
        }
    }
}