namespace FSTService;

internal sealed class SnapshotGenerationRetentionSafePointQueue
{
    internal const int DefaultCapacity = 128;

    private readonly int _capacity;
    private readonly Queue<
        PendingSnapshotGenerationRetentionSafePoint> _pending = [];
    private readonly HashSet<(long ScrapeId, long PublicationId)>
        _keys = [];
    private readonly object _sync = new();

    internal SnapshotGenerationRetentionSafePointQueue(
        int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal int Count
    {
        get
        {
            lock (_sync)
                return _pending.Count;
        }
    }

    internal bool Enqueue(
        PendingSnapshotGenerationRetentionSafePoint safePoint)
    {
        ArgumentNullException.ThrowIfNull(safePoint);
        if (safePoint.ScrapeId <= 0
            || safePoint.PublicationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(safePoint),
                "Retention safe-point identities must be positive.");
        }

        lock (_sync)
        {
            var key = (
                safePoint.ScrapeId,
                safePoint.PublicationId);
            if (_keys.Contains(key))
                return false;
            if (_pending.Count >= _capacity)
            {
                throw new InvalidOperationException(
                    $"Snapshot-generation retention safe-point queue reached its fail-closed capacity of {_capacity}; no publication was discarded.");
            }

            _pending.Enqueue(safePoint);
            _keys.Add(key);
            return true;
        }
    }

    internal bool TryPeek(
        out PendingSnapshotGenerationRetentionSafePoint?
            safePoint)
    {
        lock (_sync)
        {
            if (_pending.TryPeek(out var pending))
            {
                safePoint = pending;
                return true;
            }

            safePoint = null;
            return false;
        }
    }

    internal void CompleteTerminal(
        PendingSnapshotGenerationRetentionSafePoint safePoint)
    {
        ArgumentNullException.ThrowIfNull(safePoint);
        lock (_sync)
        {
            if (!_pending.TryPeek(out var current)
                || current != safePoint)
            {
                throw new InvalidOperationException(
                    "Retention safe-point completion did not match the queued head.");
            }

            _pending.Dequeue();
            _keys.Remove((
                safePoint.ScrapeId,
                safePoint.PublicationId));
        }
    }

    internal IReadOnlyList<
        PendingSnapshotGenerationRetentionSafePoint> Snapshot()
    {
        lock (_sync)
            return _pending.ToArray();
    }
}
