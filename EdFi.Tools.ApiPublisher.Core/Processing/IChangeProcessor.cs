using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    public interface IChangeProcessor
    {
        Task ProcessChangesAsync(ChangeProcessorConfiguration configuration);
    }
}