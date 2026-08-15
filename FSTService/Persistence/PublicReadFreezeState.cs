namespace FSTService.Persistence;

public sealed record PublicReadFreezeState(
    bool IsFrozen,
    DateTime? FrozenAt,
    long? ScrapeId,
    string? Reason)
{
    public const string PublicationCommitIntentReason = "publication-commit";
    public const string PublicationFailureIsolationPendingReason =
        "publication-isolation-pending";
    public const string PublicationCommitDeferredReason =
        "publication-commit-deferred";
    public const string MaxScoreMaintenanceReasonPrefix =
        "max-score-maintenance:v1:";

    public static PublicReadFreezeState NotFrozen { get; } = new(false, null, null, null);

    public bool PublicationCommitPending =>
        IsFrozen
        && string.Equals(
            Reason,
            PublicationCommitIntentReason,
            StringComparison.Ordinal);

    public bool PublicationFailureIsolationPending =>
        IsFrozen
        && string.Equals(
            Reason,
            PublicationFailureIsolationPendingReason,
            StringComparison.Ordinal);

    public bool PublicationCommitDeferred =>
        IsFrozen
        && string.Equals(
            Reason,
            PublicationCommitDeferredReason,
            StringComparison.Ordinal);

    public bool MaxScoreMaintenance =>
        IsFrozen
        && Reason?.StartsWith(
            MaxScoreMaintenanceReasonPrefix,
            StringComparison.Ordinal) == true;

    public bool RequiresCachedReads =>
        PublicationFailureIsolationPending
        || PublicationCommitDeferred
        || PublicationCommitPending
        || MaxScoreMaintenance;

    // Retain cache/client refresh compatibility for already-recorded maintenance freezes.
    public bool RequiresSamePublicationRefreshOnRelease =>
        IsFrozen &&
        (Reason is "path-repair-ranking-rebuild"
             or "path-repair-ranking-alignment"
         || MaxScoreMaintenance);
}
