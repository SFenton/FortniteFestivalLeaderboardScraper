using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class RegisteredLookupGraceTests
{
    [Fact]
    public void PassState_partitions_attempts_and_records_durable_timing()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var state = new RegisteredLookupPassState(time);

        state.Initialize(3);
        using (var first = state.BeginAttempt())
            first.CompleteDurable();
        time.Advance(TimeSpan.FromSeconds(12));
        using (var second = state.BeginAttempt())
            second.CompleteDurable();
        using (state.BeginAttempt())
        {
        }

        var snapshot = state.Snapshot;
        Assert.True(snapshot.StateIsValid);
        Assert.Equal(3, snapshot.Planned);
        Assert.Equal(3, snapshot.AttemptsStarted);
        Assert.Equal(0, snapshot.InFlight);
        Assert.Equal(2, snapshot.DurableCompleted);
        Assert.Equal(1, snapshot.FinishedWithoutCheckpoint);
        Assert.Equal(TimeSpan.FromSeconds(12), snapshot.MaximumDurableCompletionGap);
        Assert.Equal(12_000, snapshot.ObservedMeanDurableIntervalMilliseconds);
    }

    [Fact]
    public void PassState_initializes_once_and_lease_finishes_once()
    {
        var state = new RegisteredLookupPassState();
        state.Initialize(1);
        Assert.Throws<InvalidOperationException>(() => state.Initialize(1));

        var lease = state.BeginAttempt();
        lease.CompleteWithoutCheckpoint();
        lease.CompleteDurable();
        lease.Dispose();

        var snapshot = state.Snapshot;
        Assert.Equal(1, snapshot.FinishedWithoutCheckpoint);
        Assert.Equal(0, snapshot.DurableCompleted);
        Assert.True(snapshot.StateIsValid);
    }

    [Fact]
    public async Task PassState_observation_cannot_lose_transition()
    {
        var state = new RegisteredLookupPassState();
        state.Initialize(1);
        var observation = state.Observe();

        using var lease = state.BeginAttempt();

        await observation.Changed.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(state.Snapshot.Version > observation.Snapshot.Version);
    }

    [Fact]
    public void PassState_deadline_commits_require_current_version()
    {
        var state = new RegisteredLookupPassState();
        state.Initialize(2);
        using (var first = state.BeginAttempt())
            first.CompleteDurable();
        var staleBase = state.Observe();
        using var second = state.BeginAttempt();

        Assert.False(
            state.TryCommitBaseDecision(
                staleBase.Snapshot.Version));
        var currentBase = state.Snapshot;
        Assert.True(
            state.TryCommitBaseDecision(
                currentBase.Version));
        Assert.False(
            state.TryCommitBaseDecision(
                currentBase.Version));

        var staleBudget = state.Observe();
        second.CompleteDurable();
        Assert.False(
            state.TryCommitBudgetCancellation(
                staleBudget.Snapshot.Version));
        var currentBudget = state.Snapshot;
        Assert.True(
            state.TryCommitBudgetCancellation(
                currentBudget.Version));
        Assert.False(
            state.TryCommitBudgetCancellation(
                currentBudget.Version));
    }

    [Theory]
    [InlineData(80, 78, 1, 77, 0, true)]
    [InlineData(80, 77, 0, 77, 0, true)]
    [InlineData(80, 80, 0, 80, 0, true)]
    [InlineData(80, 76, 0, 76, 0, false)]
    [InlineData(80, 78, 0, 77, 1, false)]
    [InlineData(80, 0, 0, 0, 0, false)]
    public void Policy_applies_conservative_exact_gate(
        int planned,
        int attempts,
        int inFlight,
        int durable,
        int failed,
        bool expected)
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new RegisteredLookupPassSnapshot(
            1,
            true,
            planned,
            durable,
            attempts,
            inFlight,
            failed,
            durable > 0 ? now - TimeSpan.FromSeconds(30) : null,
            durable > 0 ? now - TimeSpan.FromSeconds(30) : null,
            null,
            durable > 0 ? 1 : null);

        var decision = RegisteredLookupGraceDecision.Evaluate(
            Policy(),
            snapshot,
            durable > 0 ? TimeSpan.FromSeconds(30) : null);

        Assert.Equal(expected, decision.Granted);
    }

    [Fact]
    public void Policy_recent_progress_boundary_is_inclusive()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new RegisteredLookupPassSnapshot(
            1, true, 3, 1, 1, 0, 0,
            now - TimeSpan.FromSeconds(90),
            now - TimeSpan.FromSeconds(90),
            null,
            1);

        Assert.True(
            RegisteredLookupGraceDecision.Evaluate(
                Policy(),
                snapshot,
                TimeSpan.FromSeconds(90)).Granted);
        Assert.False(
            RegisteredLookupGraceDecision.Evaluate(
                Policy(),
                snapshot,
                TimeSpan.FromSeconds(90) + TimeSpan.FromTicks(1)).Granted);
    }

    [Fact]
    public async Task Controller_grants_then_completes_without_reinvoking()
    {
        var time = CreateTime();
        var logger = new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredLookupPassState? observedState = null;
        var calls = 0;

        var run = controller.RunAsync<int>(
            "phase",
            "operation",
            Policy(baseTimeout: TimeSpan.FromMinutes(1)),
            (state, token) =>
            {
                calls++;
                observedState = state;
                state.Initialize(2);
                using (var lease = state.BeginAttempt())
                    lease.CompleteDurable();
                return completion.Task;
            },
            CancellationToken.None);

        await WaitForAsync(
            () => observedState?.Snapshot.DurableCompleted == 1);
        time.Advance(TimeSpan.FromMinutes(1));
        await WaitForGrantAsync(logger);
        using (var lease = observedState!.BeginAttempt())
            lease.CompleteDurable();
        completion.SetResult(42);

        Assert.Equal(42, await run);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Controller_non_durable_finish_revokes_and_preserves_partial_result()
    {
        var time = CreateTime();
        var logger = new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        RegisteredLookupPassState? observedState = null;

        var run = controller.RunAsync(
            "phase",
            "operation",
            Policy(baseTimeout: TimeSpan.FromMinutes(1)),
            async (state, token) =>
            {
                observedState = state;
                state.Initialize(2);
                using (var lease = state.BeginAttempt())
                    lease.CompleteDurable();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                    return 0;
                }
                catch (OperationCanceledException ex)
                {
                    throw new PartialResultOperationCanceledException<int>(7, ex);
                }
            },
            CancellationToken.None);

        await WaitForAsync(
            () => observedState?.Snapshot.DurableCompleted == 1);
        time.Advance(TimeSpan.FromMinutes(1));
        await WaitForGrantAsync(logger);
        using (observedState!.BeginAttempt())
        {
        }
        await DrainAsync();

        var exception = await Assert.ThrowsAsync<PartialResultFailureException<int>>(
            () => run);
        Assert.Equal(7, exception.PartialResultValue);
    }

    [Fact]
    public async Task Controller_normal_return_after_new_non_durable_finish_preserves_exact_partial_result()
    {
        var time = CreateTime();
        var logger = new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredLookupPassState? observedState = null;

        var run = controller.RunAsync(
            "phase",
            "operation",
            Policy(baseTimeout: TimeSpan.FromMinutes(1)),
            (state, token) =>
            {
                observedState = state;
                state.Initialize(2);
                using (var lease = state.BeginAttempt())
                    lease.CompleteDurable();
                return completion.Task;
            },
            CancellationToken.None);

        await WaitForAsync(
            () => observedState?.Snapshot.DurableCompleted == 1);
        time.Advance(TimeSpan.FromMinutes(1));
        await WaitForGrantAsync(logger);
        using (observedState!.BeginAttempt())
        {
        }
        completion.SetResult(37);

        var exception = await Assert.ThrowsAsync<PartialResultFailureException<int>>(
            () => run);
        Assert.Equal(37, exception.PartialResultValue);
        AssertTerminal(
            logger,
            "revoked_non_durable",
            "planned=2 durableCompleted=1 attemptsStarted=2 inFlight=0 finishedWithoutCheckpoint=1");
    }

    [Fact]
    public async Task Controller_deadline_race_accepts_fully_durable_completion_and_logs_completed_once()
    {
        var time = CreateTime();
        var logger = new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        RegisteredLookupPassState? observedState = null;

        var run = controller.RunAsync(
            "phase",
            "operation",
            Policy(
                baseTimeout: TimeSpan.FromSeconds(10),
                maxGrace: TimeSpan.FromSeconds(20),
                recent: TimeSpan.FromSeconds(20)),
            async (state, token) =>
            {
                observedState = state;
                state.Initialize(2);
                using (var lease = state.BeginAttempt())
                    lease.CompleteDurable();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                }
                catch (OperationCanceledException)
                {
                    using var lease = state.BeginAttempt();
                    lease.CompleteDurable();
                }

                return 41;
            },
            CancellationToken.None);

        await WaitForAsync(
            () => observedState?.Snapshot.DurableCompleted == 1);
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitForGrantAsync(logger);
        time.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(41, await run);
        AssertTerminal(
            logger,
            "completed",
            "planned=2 durableCompleted=2 attemptsStarted=2 inFlight=0 finishedWithoutCheckpoint=0");
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains("reason=hard_deadline_expired", StringComparison.Ordinal)
                || entry.Message.Contains("reason=grace_idle_expired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Controller_hard_timer_is_armed_before_synchronous_operation_prefix()
    {
        var time = CreateTime();
        var logger =
            new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        var run = controller.RunAsync(
            "phase",
            "operation",
            Policy(
                baseTimeout: TimeSpan.FromSeconds(10),
                maxGrace: TimeSpan.FromSeconds(20),
                recent: TimeSpan.FromSeconds(15)),
            (state, token) =>
            {
                state.Initialize(2);
                using (var first = state.BeginAttempt())
                    first.CompleteDurable();
                using var registration = token.Register(
                    () => cancelled.TrySetResult());
                started.TrySetResult();
                release.Wait();
                return Task.FromResult(53);
            },
            CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitForGrantAsync(logger);
        time.Advance(TimeSpan.FromSeconds(20));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        release.Set();

        var exception = await Assert.ThrowsAsync<
            PartialResultFailureException<int>>(
            () => run);
        Assert.Equal(53, exception.PartialResultValue);
        AssertTerminal(
            logger,
            "hard_deadline_expired",
            "planned=2 durableCompleted=1 attemptsStarted=1 inFlight=0 finishedWithoutCheckpoint=0");
    }

    [Fact]
    public async Task Controller_hard_deadline_before_grant_returns_timeout()
    {
        var time = CreateTime();
        var controller = CreateController(time);
        RegisteredLookupPassState? observedState = null;

        var run = controller.RunAsync(
            "phase",
            "operation",
            Policy(
                baseTimeout: TimeSpan.FromSeconds(10),
                maxGrace: TimeSpan.FromSeconds(20),
                recent: TimeSpan.FromSeconds(15)),
            async (state, token) =>
            {
                observedState = state;
                state.Initialize(2);
                using (var first = state.BeginAttempt())
                    first.CompleteDurable();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    time,
                    token);
                return 0;
            },
            CancellationToken.None);

        await WaitForAsync(
            () => observedState?.Snapshot.DurableCompleted == 1);
        time.Advance(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<TimeoutException>(() => run);
    }

    [Fact]
    public async Task Controller_faulted_operation_without_non_durable_attempt_logs_operation_failed()
    {
        var time = CreateTime();
        var logger =
            new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredLookupPassState? observedState = null;

        var run = controller.RunAsync<int>(
            "phase",
            "operation",
            Policy(baseTimeout: TimeSpan.FromSeconds(10)),
            async (state, token) =>
            {
                observedState = state;
                state.Initialize(2);
                using (var first = state.BeginAttempt())
                    first.CompleteDurable();
                await release.Task;
                throw new InvalidOperationException(
                    "synthetic operation failure");
            },
            CancellationToken.None);

        await WaitForAsync(
            () => observedState?.Snapshot.DurableCompleted == 1);
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitForGrantAsync(logger);
        release.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => run);
        AssertTerminal(
            logger,
            "operation_failed",
            "planned=2 durableCompleted=1 attemptsStarted=1 inFlight=0 finishedWithoutCheckpoint=0");
    }

    [Fact]
    public async Task Controller_hard_cancellation_cleanup_fault_logs_operation_failed()
    {
        var time = CreateTime();
        var logger =
            new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        RegisteredLookupPassState? observedState = null;

        var run = controller.RunAsync<int>(
            "phase",
            "operation",
            Policy(
                baseTimeout: TimeSpan.FromSeconds(10),
                maxGrace: TimeSpan.FromSeconds(20),
                recent: TimeSpan.FromSeconds(15)),
            async (state, token) =>
            {
                observedState = state;
                state.Initialize(2);
                using (var first = state.BeginAttempt())
                    first.CompleteDurable();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        time,
                        token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "synthetic cleanup failure");
                }

                return 0;
            },
            CancellationToken.None);

        await WaitForAsync(
            () => observedState?.Snapshot.DurableCompleted == 1);
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitForGrantAsync(logger);
        time.Advance(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => run);
        AssertTerminal(
            logger,
            "operation_failed",
            "planned=2 durableCompleted=1 attemptsStarted=1 inFlight=0 finishedWithoutCheckpoint=0");
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("idle")]
    [InlineData("hard")]
    public async Task Controller_failure_terminal_logs_post_unwind_snapshot_once(
        string cause)
    {
        var time = CreateTime();
        var logger = new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        RegisteredLookupAttemptLease? observedLease = null;

        var run = controller.RunAsync(
            "phase",
            "operation",
            Policy(
                baseTimeout: TimeSpan.FromSeconds(10),
                maxGrace: TimeSpan.FromSeconds(20),
                recent: TimeSpan.FromSeconds(15)),
            async (state, token) =>
            {
                state.Initialize(2);
                using (var lease = state.BeginAttempt())
                    lease.CompleteDurable();
                using var inFlight = state.BeginAttempt();
                observedLease = inFlight;
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                    return 0;
                }
                catch (OperationCanceledException ex)
                {
                    throw new PartialResultOperationCanceledException<int>(13, ex);
                }
            },
            CancellationToken.None);

        await WaitForAsync(() => observedLease is not null);
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitForGrantAsync(logger);
        if (cause == "revoke")
        {
            observedLease!.CompleteWithoutCheckpoint();
            await DrainAsync();
        }
        else
        {
            time.Advance(
                cause == "idle"
                    ? TimeSpan.FromSeconds(15)
                    : TimeSpan.FromSeconds(20));
        }

        var exception = await Assert.ThrowsAsync<PartialResultFailureException<int>>(
            () => run);
        Assert.Equal(13, exception.PartialResultValue);
        AssertTerminal(
            logger,
            cause switch
            {
                "revoke" => "revoked_non_durable",
                "idle" => "grace_idle_expired",
                _ => "hard_deadline_expired",
            },
            "planned=2 durableCompleted=1 attemptsStarted=2 inFlight=0 finishedWithoutCheckpoint=1");
    }

    [Fact]
    public async Task Controller_hard_deadline_is_immutable_despite_progress()
    {
        var time = CreateTime();
        var logger = new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        RegisteredLookupPassState? observedState = null;

        var run = controller.RunAsync(
            "phase",
            "operation",
            Policy(
                baseTimeout: TimeSpan.FromSeconds(10),
                maxGrace: TimeSpan.FromSeconds(20),
                recent: TimeSpan.FromSeconds(15)),
            async (state, token) =>
            {
                observedState = state;
                state.Initialize(3);
                using (var lease = state.BeginAttempt())
                    lease.CompleteDurable();
                await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                return 0;
            },
            CancellationToken.None);

        await WaitForAsync(
            () => observedState?.Snapshot.DurableCompleted == 1);
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitForGrantAsync(logger);
        time.Advance(TimeSpan.FromSeconds(10));
        using (var lease = observedState!.BeginAttempt())
            lease.CompleteDurable();
        await DrainAsync();
        time.Advance(TimeSpan.FromSeconds(10));
        await DrainAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => run);
    }

    [Fact]
    public async Task Controller_attempt_change_does_not_move_idle_deadline()
    {
        var time = CreateTime();
        var logger = new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        RegisteredLookupPassState? observedState = null;

        var run = controller.RunAsync(
            "phase",
            "operation",
            Policy(
                baseTimeout: TimeSpan.FromSeconds(2),
                maxGrace: TimeSpan.FromSeconds(20),
                recent: TimeSpan.FromSeconds(5)),
            async (state, token) =>
            {
                observedState = state;
                state.Initialize(2);
                using (var lease = state.BeginAttempt())
                    lease.CompleteDurable();
                await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                return 0;
            },
            CancellationToken.None);

        await WaitForAsync(
            () => observedState?.Snapshot.DurableCompleted == 1);
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitForGrantAsync(logger);
        using var inFlight = observedState!.BeginAttempt();
        await DrainAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await DrainAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => run);
    }

    [Fact]
    public async Task Controller_caller_cancellation_wins()
    {
        var time = CreateTime();
        var controller = CreateController(time);
        using var caller = new CancellationTokenSource();

        var run = controller.RunAsync(
            "phase",
            "operation",
            Policy(baseTimeout: TimeSpan.FromSeconds(10)),
            async (state, token) =>
            {
                state.Initialize(1);
                await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                return 0;
            },
            caller.Token);

        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Controller_granted_caller_cancellation_logs_final_snapshot_once()
    {
        var time = CreateTime();
        var logger = new TestLogger<RegisteredLookupGraceController>();
        var controller = CreateController(time, logger);
        using var caller = new CancellationTokenSource();
        RegisteredLookupPassState? observedState = null;

        var run = controller.RunAsync(
            "phase",
            "operation",
            Policy(baseTimeout: TimeSpan.FromSeconds(10)),
            async (state, token) =>
            {
                observedState = state;
                state.Initialize(2);
                using (var lease = state.BeginAttempt())
                    lease.CompleteDurable();
                using var inFlight = state.BeginAttempt();
                await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                return 0;
            },
            caller.Token);

        await WaitForAsync(
            () => observedState?.Snapshot.InFlight == 1);
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitForGrantAsync(logger);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        caller.Cancel();

        AssertTerminal(
            logger,
            "caller_cancelled",
            "planned=2 durableCompleted=1 attemptsStarted=2 inFlight=0 finishedWithoutCheckpoint=1");
    }

    private static RegisteredLookupGracePolicy Policy(
        TimeSpan? baseTimeout = null,
        TimeSpan? maxGrace = null,
        TimeSpan? recent = null) =>
        new(
            true,
            baseTimeout ?? TimeSpan.FromMinutes(6),
            maxGrace ?? TimeSpan.FromMinutes(2),
            recent ?? TimeSpan.FromSeconds(90),
            3);

    private static ManualTimeProvider CreateTime() =>
        new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));

    private static RegisteredLookupGraceController CreateController(
        TimeProvider timeProvider,
        ILogger? logger = null) =>
        new(timeProvider, logger ?? NullLogger.Instance);

    private static void AssertTerminal(
        TestLogger<RegisteredLookupGraceController> logger,
        string reason,
        string snapshot)
    {
        var terminals = logger.Entries
            .Where(entry => entry.Message.StartsWith(
                "registered_lookup_grace_terminal",
                StringComparison.Ordinal))
            .ToArray();
        var terminal = Assert.Single(terminals);
        Assert.Contains($"reason={reason}", terminal.Message, StringComparison.Ordinal);
        Assert.Contains(snapshot, terminal.Message, StringComparison.Ordinal);
    }

    private static async Task DrainAsync()
    {
        for (var index = 0; index < 5; index++)
            await Task.Yield();
    }

    private static async Task WaitForAsync(
        Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    "The scheduled grace operation did not reach the expected state.");
            await Task.Delay(1);
        }
    }

    private static Task WaitForGrantAsync(
        TestLogger<RegisteredLookupGraceController> logger) =>
        WaitForAsync(
            () => logger.Entries.Any(
                entry => entry.Message.Contains(
                    "registered_lookup_grace_evaluation",
                    StringComparison.Ordinal)
                    && entry.Message.Contains(
                        "decision=granted",
                        StringComparison.Ordinal)));

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow;
        private long _timestamp;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate)
                return _timestamp;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                _utcNow += amount;
                _timestamp += amount.Ticks;
                foreach (var timer in _timers.ToArray())
                    timer.CollectDueCallbacks(_utcNow, callbacks);
                _timers.RemoveAll(static timer => timer.IsDisposed);
            }

            foreach (var (callback, state) in callbacks)
                callback(state);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _period;
            private DateTimeOffset _dueAt;

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _period = period;
                _dueAt = owner._utcNow + dueTime;
            }

            public bool IsDisposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._gate)
                {
                    if (IsDisposed)
                        return false;
                    _dueAt = _owner._utcNow + dueTime;
                    _period = period;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_owner._gate)
                    IsDisposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void CollectDueCallbacks(
                DateTimeOffset now,
                List<(TimerCallback Callback, object? State)> callbacks)
            {
                if (IsDisposed || now < _dueAt)
                    return;

                callbacks.Add((_callback, _state));
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    IsDisposed = true;
                }
                else
                {
                    do
                    {
                        _dueAt += _period;
                    }
                    while (_dueAt <= now);
                }
            }
        }
    }
}
