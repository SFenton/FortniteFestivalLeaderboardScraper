using FSTService.Persistence.Maintenance;

namespace FSTService.Persistence;

public sealed partial class MetaDatabase
{
    public Task PublishRetentionWorkerConfigurationAsync(
        string instanceId, bool reportOnlyEnabled, string workerCodeSha256,
        CancellationToken ct = default) =>
        new SnapshotGenerationRetentionWorkerConfigurationStore(_ds)
            .PublishFromWorkerAsync(instanceId, reportOnlyEnabled, workerCodeSha256, ct);
}
