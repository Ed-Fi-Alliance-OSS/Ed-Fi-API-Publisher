using System.Threading.Tasks;
using Version = EdFi.Tools.ApiPublisher.Core.Helpers.Version;

namespace EdFi.Tools.ApiPublisher.Core.Isolation;

public interface ISourceIsolationApplicator
{
    Task ApplySourceSnapshotIdentifierAsync(Version sourceApiVersion);
}