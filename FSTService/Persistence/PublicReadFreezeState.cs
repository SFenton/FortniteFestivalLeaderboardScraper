namespace FSTService.Persistence;

public sealed record PublicReadFreezeState(
    bool IsFrozen,
    DateTime? FrozenAt,
    long? ScrapeId,
    string? Reason)
{
    public static PublicReadFreezeState NotFrozen { get; } = new(false, null, null, null);

    // Retain cache/client refresh compatibility for already-recorded maintenance freezes.
    public bool RequiresSamePublicationRefreshOnRelease =>
        IsFrozen &&
        Reason is "path-repair-ranking-rebuild"
            or "path-repair-ranking-alignment";
}