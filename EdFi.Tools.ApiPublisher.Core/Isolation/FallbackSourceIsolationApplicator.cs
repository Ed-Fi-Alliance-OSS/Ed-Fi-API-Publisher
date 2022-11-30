using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Helpers;

namespace EdFi.Tools.ApiPublisher.Core.Isolation;

public class FallbackSourceIsolationApplicator : ISourceIsolationApplicator
{
    public Task ApplySourceSnapshotIdentifierAsync(Version sourceApiVersion) => Task.CompletedTask;
}
