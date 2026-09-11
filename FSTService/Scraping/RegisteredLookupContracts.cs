using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace FSTService.Scraping;

internal enum RegisteredLookupOutcome
{
    Success,
    NotFound,
    InvalidLeaderboard,
    HttpFailure,
    TransportFailure,
    Cancelled,
}

internal readonly record struct RegisteredLookupPassSnapshot(
    long Version,
    bool Initialized,
    int Planned,
    int DurableCompleted,
    int AttemptsStarted,
    int InFlight,
    int FinishedWithoutCheckpoint,
    DateTimeOffset? FirstDurableCompletionTimestamp,
    DateTimeOffset? LastDurableCompletionTimestamp,
    TimeSpan? MaximumDurableCompletionGap,
    long? LastDurableCompletionMonotonicTimestamp)
{
    public int DurableRemaining => Planned - DurableCompleted;
    public int Unattempted => Planned - AttemptsStarted;

    public bool StateIsValid =>
        Planned >= 0
        && DurableCompleted >= 0
        && DurableCompleted <= AttemptsStarted
        && AttemptsStarted <= Planned
        && InFlight is 0 or 1
        && FinishedWithoutCheckpoint
            == AttemptsStarted - DurableCompleted - InFlight;

    public double? ObservedMeanDurableIntervalMilliseconds =>
        DurableCompleted > 1
        && FirstDurableCompletionTimestamp is { } first
        && LastDurableCompletionTimestamp is { } last
            ? (last - first).TotalMilliseconds / (DurableCompleted - 1)
            : null;
}

internal readonly record struct RegisteredLookupPassObservation(
    RegisteredLookupPassSnapshot Snapshot,
    Task Changed);

internal sealed class RegisteredLookupPassState
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _changed = CreateChangedSource();
    private long _version;
    private bool _initialized;
    private int _planned;
    private int _durableCompleted;
    private int _attemptsStarted;
    private int _inFlight;
    private int _finishedWithoutCheckpoint;
    private DateTimeOffset? _firstDurableCompletionTimestamp;
    private DateTimeOffset? _lastDurableCompletionTimestamp;
    private TimeSpan? _maximumDurableCompletionGap;
    private long? _lastDurableCompletionMonotonicTimestamp;
    private bool _baseDecisionCommitted;
    private bool _budgetCancellationCommitted;

    public RegisteredLookupPassState(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Initialize(int planned)
    {
        if (planned < 0)
            throw new ArgumentOutOfRangeException(nameof(planned));

        lock (_gate)
        {
            if (_initialized)
                throw new InvalidOperationException("The registered lookup pass is already initialized.");

            _initialized = true;
            _planned = planned;
            SignalChanged();
        }
    }

    public RegisteredLookupAttemptLease BeginAttempt()
    {
        lock (_gate)
        {
            if (!_initialized)
                throw new InvalidOperationException("The registered lookup pass is not initialized.");
            if (_inFlight != 0)
                throw new InvalidOperationException("Only one registered lookup attempt may be in flight.");
            if (_attemptsStarted >= _planned)
                throw new InvalidOperationException("No admitted registered lookup attempts remain.");

            _attemptsStarted++;
            _inFlight = 1;
            SignalChanged();
            return new RegisteredLookupAttemptLease(this);
        }
    }

    public RegisteredLookupPassObservation Observe()
    {
        lock (_gate)
            return new RegisteredLookupPassObservation(CreateSnapshot(), _changed.Task);
    }

    public RegisteredLookupPassSnapshot Snapshot => Observe().Snapshot;

    public bool TryCommitBaseDecision(long expectedVersion)
    {
        lock (_gate)
        {
            if (_baseDecisionCommitted
                || _version != expectedVersion)
            {
                return false;
            }

            _baseDecisionCommitted = true;
            return true;
        }
    }

    public bool TryCommitBudgetCancellation(long expectedVersion)
    {
        lock (_gate)
        {
            if (_budgetCancellationCommitted
                || _version != expectedVersion)
            {
                return false;
            }

            _budgetCancellationCommitted = true;
            return true;
        }
    }

    internal void FinishAttempt(bool durable)
    {
        lock (_gate)
        {
            if (_inFlight == 0)
                return;

            _inFlight = 0;
            if (durable)
            {
                var now = _timeProvider.GetUtcNow();
                if (_lastDurableCompletionTimestamp is { } previous)
                {
                    if (now < previous)
                        now = previous;
                    var gap = now - previous;
                    if (_maximumDurableCompletionGap is null
                        || gap > _maximumDurableCompletionGap)
                    {
                        _maximumDurableCompletionGap = gap;
                    }
                }

                _firstDurableCompletionTimestamp ??= now;
                _lastDurableCompletionTimestamp = now;
                _lastDurableCompletionMonotonicTimestamp =
                    _timeProvider.GetTimestamp();
                _durableCompleted++;
            }
            else
            {
                _finishedWithoutCheckpoint++;
            }

            SignalChanged();
        }
    }

    private RegisteredLookupPassSnapshot CreateSnapshot() => new(
        _version,
        _initialized,
        _planned,
        _durableCompleted,
        _attemptsStarted,
        _inFlight,
        _finishedWithoutCheckpoint,
        _firstDurableCompletionTimestamp,
        _lastDurableCompletionTimestamp,
        _maximumDurableCompletionGap,
        _lastDurableCompletionMonotonicTimestamp);

    private void SignalChanged()
    {
        _version++;
        var completed = _changed;
        _changed = CreateChangedSource();
        completed.TrySetResult();
    }

    private static TaskCompletionSource CreateChangedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class RegisteredLookupAttemptLease : IDisposable
{
    private RegisteredLookupPassState? _owner;

    internal RegisteredLookupAttemptLease(RegisteredLookupPassState owner)
    {
        _owner = owner;
    }

    public void CompleteDurable()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.FinishAttempt(durable: true);
    }

    public void CompleteWithoutCheckpoint()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.FinishAttempt(durable: false);
    }

    public void Dispose() => CompleteWithoutCheckpoint();
}

internal sealed record RegisteredLookupGracePolicy(
    bool Enabled,
    TimeSpan BaseTimeout,
    TimeSpan MaxGrace,
    TimeSpan RecentProgressWindow,
    int MaxDurableRemaining);

internal enum RegisteredLookupGraceDecisionReason
{
    Disabled,
    OperationCompleted,
    Uninitialized,
    InvalidState,
    NoDurableProgress,
    FinishedWithoutCheckpoint,
    RemainingAboveLimit,
    StaleDurableProgress,
    Granted,
}

internal enum RegisteredLookupGraceTerminalReason
{
    Completed,
    OperationFailed,
    RevokedNonDurable,
    GraceIdleExpired,
    HardDeadlineExpired,
    CallerCancelled,
}

internal readonly record struct RegisteredLookupGraceDecision(
    bool Granted,
    RegisteredLookupGraceDecisionReason Reason,
    RegisteredLookupPassSnapshot Snapshot,
    TimeSpan? LastDurableProgressAge)
{
    public static RegisteredLookupGraceDecision Evaluate(
        RegisteredLookupGracePolicy policy,
        RegisteredLookupPassSnapshot snapshot,
        TimeSpan? lastDurableProgressAge)
    {
        if (!policy.Enabled)
            return new(false, RegisteredLookupGraceDecisionReason.Disabled, snapshot, lastDurableProgressAge);
        if (!snapshot.Initialized)
            return new(false, RegisteredLookupGraceDecisionReason.Uninitialized, snapshot, lastDurableProgressAge);
        if (!snapshot.StateIsValid)
            return new(false, RegisteredLookupGraceDecisionReason.InvalidState, snapshot, lastDurableProgressAge);
        if (snapshot.Planned <= 0 || snapshot.DurableCompleted <= 0)
            return new(false, RegisteredLookupGraceDecisionReason.NoDurableProgress, snapshot, lastDurableProgressAge);
        if (snapshot.FinishedWithoutCheckpoint > 0)
            return new(false, RegisteredLookupGraceDecisionReason.FinishedWithoutCheckpoint, snapshot, lastDurableProgressAge);
        if (snapshot.DurableRemaining < 0
            || snapshot.DurableRemaining > policy.MaxDurableRemaining)
        {
            return new(false, RegisteredLookupGraceDecisionReason.RemainingAboveLimit, snapshot, lastDurableProgressAge);
        }
        if (lastDurableProgressAge is null
            || lastDurableProgressAge < TimeSpan.Zero
            || lastDurableProgressAge > policy.RecentProgressWindow)
        {
            return new(false, RegisteredLookupGraceDecisionReason.StaleDurableProgress, snapshot, lastDurableProgressAge);
        }

        return new(true, RegisteredLookupGraceDecisionReason.Granted, snapshot, lastDurableProgressAge);
    }
}

internal sealed class EpicLeaderboardUnavailableException : Exception
{
    public const string ErrorCode =
        "com.epicgames.events.invalid_leaderboard";

    public EpicLeaderboardUnavailableException()
        : base("The requested Epic leaderboard is not currently available.")
    {
    }

    public static bool IsExactInvalidLeaderboard(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(
                    "errorCode",
                    out var errorCode)
                && errorCode.ValueKind == JsonValueKind.String
                && string.Equals(
                    errorCode.GetString(),
                    ErrorCode,
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal static class RegisteredLookupInstrumentation
{
    private static readonly Meter Meter =
        new("FSTService.RegisteredBandLookups");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>(
            "fst.registered_band.logical_lookup.duration",
            "ms",
            "End-to-end elapsed duration of one registered-band logical lookup.");

    public static Stopwatch Start() => Stopwatch.StartNew();

    public static void Record(
        Stopwatch stopwatch,
        string phase,
        RegisteredLookupOutcome outcome)
    {
        stopwatch.Stop();
        Duration.Record(
            stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>(
                "outcome",
                outcome.ToString().ToLowerInvariant()));
    }

    public static RegisteredLookupOutcome ClassifyFailure(Exception? exception)
        => exception switch
        {
            EpicLeaderboardUnavailableException =>
                RegisteredLookupOutcome.InvalidLeaderboard,
            HttpRequestException =>
                RegisteredLookupOutcome.HttpFailure,
            _ => RegisteredLookupOutcome.TransportFailure,
        };
}

internal interface IPartialResultFailure
{
    object PartialResult { get; }
}

internal sealed class PartialResultFailureException<T> : Exception,
    IPartialResultFailure
{
    public PartialResultFailureException(
        T partialResult,
        Exception innerException)
        : base(innerException.Message, innerException)
    {
        PartialResultValue = partialResult;
    }

    public T PartialResultValue { get; }
    object IPartialResultFailure.PartialResult => PartialResultValue!;
}

internal sealed class PartialResultOperationCanceledException<T>
    : OperationCanceledException
{
    public PartialResultOperationCanceledException(
        T partialResult,
        OperationCanceledException innerException)
        : base(innerException.Message, innerException, innerException.CancellationToken)
    {
        PartialResult = partialResult;
    }

    public T PartialResult { get; }
}
