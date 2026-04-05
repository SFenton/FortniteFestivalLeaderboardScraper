using System.Collections.Concurrent;
using System.Diagnostics;
using FortniteFestival.Core.Scraping;
using FSTService.Scraping;

namespace SongMachineHarness;

public sealed class InstrumentedQuerier : ILeaderboardQuerier
{
    private readonly ILeaderboardQuerier _inner;
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly ResilientHttpExecutor? _executor;
    private readonly ConcurrentQueue<CallEvent> _events = new();
    private readonly Stopwatch _sw;
    private int _nextCallId;

    public InstrumentedQuerier(ILeaderboardQuerier inner, AdaptiveConcurrencyLimiter limiter, Stopwatch sw, ResilientHttpExecutor? executor = null)
    {
        _inner = inner;
        _limiter = limiter;
        _sw = sw;
        _executor = executor;
    }

    public IReadOnlyCollection<CallEvent> Events => _events;

    // ─── Instrumented methods (used by SongProcessingMachine) ───

    public async Task<List<LeaderboardEntry>> LookupMultipleAccountsAsync(
        string songId, string instrument, IReadOnlyList<string> targetAccountIds,
        string accessToken, string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default)
    {
        var callId = Interlocked.Increment(ref _nextCallId);
        long wireBefore = _executor?.TotalHttpSends ?? 0;
        var evt = new CallEvent
        {
            CallId = callId,
            Type = "alltime",
            SongId = songId,
            Instrument = instrument,
            BatchSize = targetAccountIds.Count,
            StartMs = _sw.ElapsedMilliseconds,
            InFlightAtStart = _limiter.InFlight,
        };

        try
        {
            var result = await _inner.LookupMultipleAccountsAsync(
                songId, instrument, targetAccountIds, accessToken, callerAccountId, limiter, ct);

            evt.EndMs = _sw.ElapsedMilliseconds;
            evt.DurationMs = evt.EndMs - evt.StartMs;
            evt.ResultCount = result.Count;
            evt.Success = true;
            evt.InFlightAtEnd = _limiter.InFlight;
            evt.PaginationPages = (int)((_executor?.TotalHttpSends ?? 0) - wireBefore);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            evt.EndMs = _sw.ElapsedMilliseconds;
            evt.DurationMs = evt.EndMs - evt.StartMs;
            evt.Success = false;
            evt.InFlightAtEnd = _limiter.InFlight;
            evt.PaginationPages = (int)((_executor?.TotalHttpSends ?? 0) - wireBefore);
            evt.ExceptionType = ex.GetType().Name;
            evt.ExceptionMessage = ex.Message;
            throw;
        }
        finally
        {
            _events.Enqueue(evt);
        }
    }

    public async Task<List<SessionHistoryEntry>> LookupMultipleAccountSessionsAsync(
        string songId, string instrument, string seasonPrefix,
        IReadOnlyList<string> targetAccountIds,
        string accessToken, string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default)
    {
        var callId = Interlocked.Increment(ref _nextCallId);
        long wireBefore = _executor?.TotalHttpSends ?? 0;
        var evt = new CallEvent
        {
            CallId = callId,
            Type = "seasonal",
            SongId = songId,
            Instrument = instrument,
            BatchSize = targetAccountIds.Count,
            StartMs = _sw.ElapsedMilliseconds,
            InFlightAtStart = _limiter.InFlight,
            Season = seasonPrefix,
        };

        try
        {
            var result = await _inner.LookupMultipleAccountSessionsAsync(
                songId, instrument, seasonPrefix, targetAccountIds, accessToken, callerAccountId, limiter, ct);

            evt.EndMs = _sw.ElapsedMilliseconds;
            evt.DurationMs = evt.EndMs - evt.StartMs;
            evt.ResultCount = result.Count;
            evt.Success = true;
            evt.InFlightAtEnd = _limiter.InFlight;
            evt.PaginationPages = (int)((_executor?.TotalHttpSends ?? 0) - wireBefore);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            evt.EndMs = _sw.ElapsedMilliseconds;
            evt.DurationMs = evt.EndMs - evt.StartMs;
            evt.Success = false;
            evt.InFlightAtEnd = _limiter.InFlight;
            evt.PaginationPages = (int)((_executor?.TotalHttpSends ?? 0) - wireBefore);
            evt.ExceptionType = ex.GetType().Name;
            evt.ExceptionMessage = ex.Message;
            throw;
        }
        finally
        {
            _events.Enqueue(evt);
        }
    }

    // ─── Pass-through methods (not used by SongProcessingMachine) ───

    public Task<LeaderboardEntry?> LookupAccountAsync(
        string songId, string instrument, string targetAccountId,
        string accessToken, string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default)
        => _inner.LookupAccountAsync(songId, instrument, targetAccountId, accessToken, callerAccountId, limiter, ct);

    public Task<(LeaderboardEntry? Target, List<LeaderboardEntry> Neighbors)> LookupAccountWithNeighborsAsync(
        string songId, string instrument, string targetAccountId,
        string accessToken, string callerAccountId,
        int neighborRadius = 50,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default)
        => _inner.LookupAccountWithNeighborsAsync(songId, instrument, targetAccountId, accessToken, callerAccountId, neighborRadius, limiter, ct);

    public Task<LeaderboardEntry?> LookupSeasonalAsync(
        string songId, string instrument, string windowId,
        string targetAccountId, string accessToken, string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default)
        => _inner.LookupSeasonalAsync(songId, instrument, windowId, targetAccountId, accessToken, callerAccountId, limiter, ct);

    public Task<List<SessionHistoryEntry>?> LookupSeasonalSessionsAsync(
        string songId, string instrument, string windowId,
        string targetAccountId, string accessToken, string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default)
        => _inner.LookupSeasonalSessionsAsync(songId, instrument, windowId, targetAccountId, accessToken, callerAccountId, limiter, ct);
}
