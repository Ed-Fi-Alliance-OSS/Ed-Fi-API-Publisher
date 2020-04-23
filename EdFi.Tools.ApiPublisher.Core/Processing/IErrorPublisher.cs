using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    public interface IErrorPublisher
    {
        Task PublishErrorsAsync(ErrorItemMessage[] messages);

        long GetPublishedErrorCount();
    }
}