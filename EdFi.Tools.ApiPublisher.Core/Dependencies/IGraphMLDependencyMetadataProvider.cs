using System.Threading.Tasks;
using System.Xml.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Dependencies;

public interface IGraphMLDependencyMetadataProvider
{
    Task<(XElement, XNamespace)> GetDependencyMetadataAsync();
}
