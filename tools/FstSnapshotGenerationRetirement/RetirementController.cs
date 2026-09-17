namespace FstSnapshotGenerationRetirement;

public sealed class RetirementController
{
    private readonly IRetirementDatabase _database;
    private readonly IRetirementRuntimeIdentityProvider
        _identityProvider;

    public RetirementController(
        IRetirementDatabase database,
        IRetirementRuntimeIdentityProvider identityProvider)
    {
        _database = database;
        _identityProvider = identityProvider;
    }

    public async Task<RetirementStatus> StatusAsync(
        CancellationToken ct = default)
    {
        var codeIdentity =
            await _identityProvider.CaptureAsync(
                requireCleanRepository: false,
                ct);
        return await _database.ReadStatusAsync(
            codeIdentity,
            ct);
    }

    public async Task<SnapshotGenerationRetirementPolicy>
        AuthorizePolicyEpochAsync(
            RetirementAuthorizationRequest request,
            CancellationToken ct = default)
    {
        var codeIdentity =
            await _identityProvider.CaptureAsync(
                requireCleanRepository: true,
                ct);
        return await _database.AuthorizePolicyEpochAsync(
            request,
            codeIdentity,
            ct);
    }

    public Task<RetirementReconcileResult>
        ReconcileAsync(
            CancellationToken ct = default) =>
        _database.ReconcileAsync(ct);

    public Task<RetirementReconcileResult>
        DeactivatePolicyEpochAsync(
            CancellationToken ct = default) =>
        _database.DeactivatePolicyEpochAsync(ct);

    public async Task<SnapshotGenerationRetirementJob>
        PlanCycleAsync(
            CancellationToken ct = default)
    {
        var codeIdentity =
            await _identityProvider.CaptureAsync(
                requireCleanRepository: true,
                ct);
        return await _database.PlanCycleAsync(
            codeIdentity,
            ct);
    }
}
