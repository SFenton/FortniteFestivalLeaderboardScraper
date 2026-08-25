namespace FSTService.Scraping;

/// <summary>
/// Ambient publication read scope for <see cref="PathDataStore"/>.
/// The scope flows with the asynchronous control flow, so concurrent requests
/// and background tasks stay isolated from each other.
/// </summary>
public sealed class PathDataStorePublicationScope : IDisposable
{
    private static readonly AsyncLocal<long?> Current = new();

    internal static readonly IDisposable NoOp = new NoOpScope();

    private readonly long? _previous;
    private bool _disposed;

    private PathDataStorePublicationScope(long? previous)
    {
        _previous = previous;
    }

    /// <summary>Publication ID scoped to the current asynchronous flow.</summary>
    public static long? CurrentPublicationId => Current.Value;

    internal static PathDataStorePublicationScope Begin(long publicationId)
    {
        if (publicationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(publicationId),
                publicationId,
                "A publication read scope requires a positive publication ID.");
        }

        var scope = new PathDataStorePublicationScope(Current.Value);
        Current.Value = publicationId;
        return scope;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Current.Value = _previous;
    }

    private sealed class NoOpScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
