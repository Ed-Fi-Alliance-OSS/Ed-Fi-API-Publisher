using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    public interface IChangeVersionProcessedWriter
    {
        Task SetProcessedChangeVersionAsync(
            string sourceConnectionName,
            string targetConnectionName,
            long changeVersion,
            IConfigurationSection configurationStoreSection);
    }
}