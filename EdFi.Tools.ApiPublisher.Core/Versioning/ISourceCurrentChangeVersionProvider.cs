using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Versioning;

public interface ISourceCurrentChangeVersionProvider
{
    Task<long?> GetCurrentChangeVersionAsync();
}