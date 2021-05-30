using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Processing;
using log4net;

namespace EdFi.Tools.ApiPublisher.Core.Configuration.Plaintext
{
    public class PlaintextChangeVersionProcessedWriter : IChangeVersionProcessedWriter
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(PlaintextChangeVersionProcessedWriter));
        
        public Task SetProcessedChangeVersionAsync(string sourceConnectionName, string targetConnectionName, long changeVersion)
        {
            _logger.Warn("Plaintext connections don't support writing back updated change versions.");
            return Task.FromResult(0);
        }
    }
}