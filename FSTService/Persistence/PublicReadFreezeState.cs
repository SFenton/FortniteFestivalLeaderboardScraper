namespace FSTService.Persistence;

public sealed record PublicReadFreezeState(
    bool IsFrozen,
    DateTime? FrozenAt,
    long? ScrapeId,
    string? Reason)
{
    public static PublicReadFreezeState NotFrozen { get; } = new(false, null, null, null);
}