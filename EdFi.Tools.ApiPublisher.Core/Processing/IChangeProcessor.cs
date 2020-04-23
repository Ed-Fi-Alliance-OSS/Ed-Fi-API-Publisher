using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    public interface IChangeProcessor
    {
        void ProcessChanges(ChangeProcessorRuntimeConfiguration configuration);
    }
}