using EdFi.Tools.ApiPublisher.Core.Processing;
using Microsoft.Extensions.Configuration;
using Serilog;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Configuration.Plaintext
{
    public class PlaintextChangeVersionProcessedWriter : IChangeVersionProcessedWriter
    {
        private readonly ILogger _logger = Log.Logger.ForContext(typeof(PlaintextChangeVersionProcessedWriter));
        
        public Task SetProcessedChangeVersionAsync(
            string sourceConnectionName,
            string targetConnectionName,
            long changeVersion,
            IConfigurationSection configurationStoreSection)
        {
            _logger.Warning("Plaintext connections don't support writing back updated change versions.");
            return Task.FromResult(0);
        }
    }
}