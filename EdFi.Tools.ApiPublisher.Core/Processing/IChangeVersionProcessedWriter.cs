using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    public interface IChangeVersionProcessedWriter
    {
        Task SetProcessedChangeVersionAsync(string sourceConnectionName, string targetConnectionName, long changeVersion);
    }
}