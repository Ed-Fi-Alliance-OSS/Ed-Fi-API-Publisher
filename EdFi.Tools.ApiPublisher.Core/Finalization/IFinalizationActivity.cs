using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Finalization;

public interface IFinalizationActivity
{
    Task Execute();
}
