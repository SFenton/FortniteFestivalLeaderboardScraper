namespace FSTService.Scraping;

internal sealed class RegisteredLookupGraceController
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _log;

    public RegisteredLookupGraceController(
        TimeProvider timeProvider,
        ILogger log)
    {
        _timeProvider = timeProvider;
        _log = log;
    }

    public async Task<T> RunAsync<T>(
        string phase,
        string operationName,
        RegisteredLookupGracePolicy policy,
        Func<RegisteredLookupPassState, CancellationToken, Task<T>> operation,
        CancellationToken callerToken)
    {
        var phaseStart = _timeProvider.GetTimestamp();
        var state = new RegisteredLookupPassState(_timeProvider);
        var hardDeadline = policy.BaseTimeout + policy.MaxGrace;
        using var hardDeadlineCts =
            new CancellationTokenSource(
                hardDeadline,
                _timeProvider);
        using var phaseCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
                hardDeadlineCts.Token);
        using var callerCancellationRegistration =
            RegisterCancellation(callerToken, out var callerCancelled);
        using var baseDelayCts = new CancellationTokenSource();
        var baseDelay = Task.Delay(
            policy.BaseTimeout,
            _timeProvider,
            baseDelayCts.Token);
        using var hardDeadlineRegistration =
            RegisterCancellation(
                hardDeadlineCts.Token,
                out var hardDeadlineReached);
        var operationTask = InvokeOperation(
            operation,
            state,
            phaseCts.Token);
        var baseWinner = await Task.WhenAny(
            operationTask,
            callerCancelled,
            baseDelay);
        baseDelayCts.Cancel();

        if (callerToken.IsCancellationRequested)
        {
            phaseCts.Cancel();
            await AwaitCallerCancellationAsync(operationTask, callerToken);
        }

        if (hardDeadlineCts.IsCancellationRequested)
        {
            phaseCts.Cancel();
            return await AwaitBudgetCancellationAsync(
                operationTask,
                state,
                operationName,
                hardDeadline,
                $"Post-scrape {operationName} timed out at the immutable grace hard deadline.",
                callerToken);
        }

        if (operationTask.IsCompleted)
        {
            if (baseWinner == baseDelay)
            {
                var completedAt = _timeProvider.GetTimestamp();
                var completedSnapshot = state.Snapshot;
                var completedAge =
                    completedSnapshot.LastDurableCompletionMonotonicTimestamp
                        is { } completedDurableTimestamp
                        ? _timeProvider.GetElapsedTime(
                            completedDurableTimestamp,
                            completedAt)
                        : (TimeSpan?)null;
                LogEvaluation(
                    phase,
                    new RegisteredLookupGraceDecision(
                        false,
                        RegisteredLookupGraceDecisionReason.OperationCompleted,
                        completedSnapshot,
                        completedAge),
                    policy,
                    phaseStart,
                    completedAt,
                    RegisteredLookupGraceDecisionReason.OperationCompleted);
            }

            return await AwaitPreGrantOperationCompletionAsync(
                operationTask,
                state,
                operationName,
                hardDeadlineCts,
                hardDeadline,
                callerToken);
        }

        RegisteredLookupPassObservation observation;
        RegisteredLookupGraceDecision decision;
        long now;
        while (true)
        {
            if (callerToken.IsCancellationRequested)
            {
                phaseCts.Cancel();
                await AwaitCallerCancellationAsync(
                    operationTask,
                    callerToken);
            }

            if (hardDeadlineCts.IsCancellationRequested)
            {
                phaseCts.Cancel();
                return await AwaitBudgetCancellationAsync(
                    operationTask,
                    state,
                    operationName,
                    hardDeadline,
                    $"Post-scrape {operationName} timed out at the immutable grace hard deadline.",
                    callerToken);
            }

            if (operationTask.IsCompleted)
            {
                return await AwaitPreGrantOperationCompletionAsync(
                    operationTask,
                    state,
                    operationName,
                    hardDeadlineCts,
                    hardDeadline,
                    callerToken);
            }

            now = _timeProvider.GetTimestamp();
            observation = state.Observe();
            var durableProgressAge =
                observation.Snapshot
                    .LastDurableCompletionMonotonicTimestamp
                    is { } durableTimestamp
                    ? _timeProvider.GetElapsedTime(
                        durableTimestamp,
                        now)
                    : (TimeSpan?)null;
            decision = RegisteredLookupGraceDecision.Evaluate(
                policy,
                observation.Snapshot,
                durableProgressAge);
            if (state.TryCommitBaseDecision(
                    observation.Snapshot.Version))
            {
                break;
            }

            await Task.Yield();
        }

        LogEvaluation(
            phase,
            decision,
            policy,
            phaseStart,
            now,
            decision.Reason);

        if (!decision.Granted)
        {
            phaseCts.Cancel();
            return await AwaitBudgetCancellationAsync(
                operationTask,
                state,
                operationName,
                policy.BaseTimeout,
                $"Post-scrape {operationName} timed out after {policy.BaseTimeout}.",
                callerToken);
        }

        var grantTimestamp = now;
        var phaseElapsed = _timeProvider.GetElapsedTime(phaseStart, now);
        var idleDeadline = Min(
            hardDeadline,
            phaseElapsed + policy.RecentProgressWindow);
        var lastDurableCompleted = observation.Snapshot.DurableCompleted;

        while (true)
        {
            observation = state.Observe();
            var snapshot = observation.Snapshot;

            if (callerToken.IsCancellationRequested)
            {
                await AwaitGrantedCallerCancellationAsync(
                    phase,
                    operationTask,
                    state,
                    phaseCts,
                    callerToken,
                    grantTimestamp);
            }

            now = _timeProvider.GetTimestamp();
            phaseElapsed = _timeProvider.GetElapsedTime(phaseStart, now);
            if (hardDeadlineCts.IsCancellationRequested
                || phaseElapsed >= hardDeadline)
            {
                return await AwaitGrantedBudgetCancellationAsync(
                    phase,
                    RegisteredLookupGraceTerminalReason.HardDeadlineExpired,
                    operationTask,
                    state,
                    operationName,
                    phaseCts,
                    hardDeadline,
                    $"Post-scrape {operationName} timed out at the immutable grace hard deadline.",
                    callerToken,
                    grantTimestamp);
            }

            if (operationTask.IsCompleted)
            {
                return await AwaitGrantedOperationCompletionAsync(
                    phase,
                    operationTask,
                    state,
                    operationName,
                    phaseCts,
                    callerToken,
                    grantTimestamp);
            }

            if (snapshot.FinishedWithoutCheckpoint > 0)
            {
                return await AwaitGrantedBudgetCancellationAsync(
                    phase,
                    RegisteredLookupGraceTerminalReason.RevokedNonDurable,
                    operationTask,
                    state,
                    operationName,
                    phaseCts,
                    _timeProvider.GetElapsedTime(phaseStart),
                    $"Post-scrape {operationName} remaining-work grace was revoked after a non-durable lookup.",
                    callerToken,
                    grantTimestamp);
            }

            if (snapshot.DurableCompleted > lastDurableCompleted
                && snapshot.LastDurableCompletionMonotonicTimestamp
                    is { } completion)
            {
                lastDurableCompleted = snapshot.DurableCompleted;
                idleDeadline = Min(
                    hardDeadline,
                    _timeProvider.GetElapsedTime(phaseStart, completion)
                        + policy.RecentProgressWindow);
            }

            if (phaseElapsed >= idleDeadline)
            {
                if (!state.TryCommitBudgetCancellation(
                        snapshot.Version))
                {
                    continue;
                }

                return await AwaitGrantedBudgetCancellationAsync(
                    phase,
                    RegisteredLookupGraceTerminalReason.GraceIdleExpired,
                    operationTask,
                    state,
                    operationName,
                    phaseCts,
                    phaseElapsed,
                    $"Post-scrape {operationName} timed out after the grace idle deadline.",
                    callerToken,
                    grantTimestamp);
            }

            using var deadlineCts = new CancellationTokenSource();
            var idleDelay = DelayUntil(
                phaseStart,
                idleDeadline,
                deadlineCts.Token);
            _ = await Task.WhenAny(
                operationTask,
                callerCancelled,
                observation.Changed,
                idleDelay,
                hardDeadlineReached);
            deadlineCts.Cancel();
        }
    }

    private async Task<T> AwaitGrantedOperationCompletionAsync<T>(
        string phase,
        Task<T> operationTask,
        RegisteredLookupPassState state,
        string operationName,
        CancellationTokenSource phaseCts,
        CancellationToken callerToken,
        long grantTimestamp)
    {
        T result;
        try
        {
            result = await operationTask;
            callerToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
            when (callerToken.IsCancellationRequested)
        {
            LogTerminal(
                phase,
                RegisteredLookupGraceTerminalReason.CallerCancelled,
                state.Snapshot,
                grantTimestamp);
            throw;
        }
        catch
        {
            var failedSnapshot = state.Snapshot;
            LogTerminal(
                phase,
                failedSnapshot.FinishedWithoutCheckpoint > 0
                    ? RegisteredLookupGraceTerminalReason.RevokedNonDurable
                    : RegisteredLookupGraceTerminalReason.OperationFailed,
                failedSnapshot,
                grantTimestamp);
            throw;
        }

        var snapshot = state.Snapshot;
        if (IsFullyDurable(snapshot))
        {
            LogTerminal(
                phase,
                RegisteredLookupGraceTerminalReason.Completed,
                snapshot,
                grantTimestamp);
            return result;
        }

        phaseCts.Cancel();
        LogTerminal(
            phase,
            RegisteredLookupGraceTerminalReason.RevokedNonDurable,
            snapshot,
            grantTimestamp);
        throw new PartialResultFailureException<T>(
            result,
            new TimeoutException(
                $"Post-scrape {operationName} remaining-work grace ended with incomplete durable lookup work."));
    }

    private async Task<T> AwaitGrantedBudgetCancellationAsync<T>(
        string phase,
        RegisteredLookupGraceTerminalReason terminalReason,
        Task<T> operationTask,
        RegisteredLookupPassState state,
        string operationName,
        CancellationTokenSource phaseCts,
        TimeSpan elapsed,
        string message,
        CancellationToken callerToken,
        long grantTimestamp)
    {
        phaseCts.Cancel();
        var budgetCancellationObserved = false;
        try
        {
            var result = await AwaitBudgetCancellationAsync(
                operationTask,
                state,
                operationName,
                elapsed,
                message,
                callerToken,
                () => budgetCancellationObserved = true);
            LogTerminal(
                phase,
                RegisteredLookupGraceTerminalReason.Completed,
                state.Snapshot,
                grantTimestamp);
            return result;
        }
        catch (OperationCanceledException)
            when (callerToken.IsCancellationRequested)
        {
            LogTerminal(
                phase,
                RegisteredLookupGraceTerminalReason.CallerCancelled,
                state.Snapshot,
                grantTimestamp);
            throw;
        }
        catch
        {
            var snapshot = state.Snapshot;
            LogTerminal(
                phase,
                budgetCancellationObserved
                    || snapshot.FinishedWithoutCheckpoint > 0
                    ? terminalReason
                    : RegisteredLookupGraceTerminalReason.OperationFailed,
                snapshot,
                grantTimestamp);
            throw;
        }
    }

    private async Task AwaitGrantedCallerCancellationAsync<T>(
        string phase,
        Task<T> operationTask,
        RegisteredLookupPassState state,
        CancellationTokenSource phaseCts,
        CancellationToken callerToken,
        long grantTimestamp)
    {
        phaseCts.Cancel();
        try
        {
            await AwaitCallerCancellationAsync(operationTask, callerToken);
        }
        finally
        {
            LogTerminal(
                phase,
                RegisteredLookupGraceTerminalReason.CallerCancelled,
                state.Snapshot,
                grantTimestamp);
        }
    }

    private static bool IsFullyDurable(
        RegisteredLookupPassSnapshot snapshot) =>
        snapshot.Initialized
        && snapshot.StateIsValid
        && snapshot.DurableCompleted == snapshot.Planned
        && snapshot.FinishedWithoutCheckpoint == 0;

    private static Task<T> InvokeOperation<T>(
        Func<RegisteredLookupPassState, CancellationToken, Task<T>> operation,
        RegisteredLookupPassState state,
        CancellationToken token) =>
        Task.Run(
            () => operation(state, token),
            CancellationToken.None);

    private async Task<T> AwaitPreGrantOperationCompletionAsync<T>(
        Task<T> operationTask,
        RegisteredLookupPassState state,
        string operationName,
        CancellationTokenSource hardDeadlineCts,
        TimeSpan hardDeadline,
        CancellationToken callerToken)
    {
        try
        {
            return await operationTask;
        }
        catch (OperationCanceledException)
        {
            callerToken.ThrowIfCancellationRequested();
            if (!hardDeadlineCts.IsCancellationRequested)
                throw;

            return await AwaitBudgetCancellationAsync(
                operationTask,
                state,
                operationName,
                hardDeadline,
                $"Post-scrape {operationName} timed out at the immutable grace hard deadline.",
                callerToken);
        }
    }

    private async Task<T> AwaitBudgetCancellationAsync<T>(
        Task<T> operationTask,
        RegisteredLookupPassState state,
        string operationName,
        TimeSpan elapsed,
        string message,
        CancellationToken callerToken,
        Action? markBudgetCancellation = null)
    {
        try
        {
            var result = await operationTask;
            callerToken.ThrowIfCancellationRequested();
            var snapshot = state.Snapshot;
            if (IsFullyDurable(snapshot))
            {
                return result;
            }

            markBudgetCancellation?.Invoke();
            throw new PartialResultFailureException<T>(
                result,
                new TimeoutException(message));
        }
        catch (PartialResultOperationCanceledException<T> ex)
        {
            callerToken.ThrowIfCancellationRequested();
            markBudgetCancellation?.Invoke();
            _log.LogWarning(
                "Post-scrape {OperationName} timed out after {Timeout}. Continuing with downstream phases using the partial result; work will retry next pass.",
                operationName,
                elapsed);
            throw new PartialResultFailureException<T>(
                ex.PartialResult,
                new TimeoutException(message, ex));
        }
        catch (OperationCanceledException ex)
        {
            callerToken.ThrowIfCancellationRequested();
            markBudgetCancellation?.Invoke();
            _log.LogWarning(
                "Post-scrape {OperationName} timed out after {Timeout}. Continuing with downstream ranking and notification phases; work will retry next pass.",
                operationName,
                elapsed);
            throw new TimeoutException(message, ex);
        }
    }

    private static async Task AwaitCallerCancellationAsync<T>(
        Task<T> operationTask,
        CancellationToken callerToken)
    {
        try
        {
            _ = await operationTask;
        }
        catch (Exception) when (callerToken.IsCancellationRequested)
        {
        }

        callerToken.ThrowIfCancellationRequested();
    }

    private Task DelayUntil(
        long phaseStart,
        TimeSpan deadline,
        CancellationToken token)
    {
        var delay = deadline - _timeProvider.GetElapsedTime(phaseStart);
        return Task.Delay(
            delay > TimeSpan.Zero ? delay : TimeSpan.Zero,
            _timeProvider,
            token);
    }

    private static CancellationTokenRegistration RegisterCancellation(
        CancellationToken token,
        out Task cancellationTask)
    {
        if (!token.CanBeCanceled)
        {
            cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan);
            return default;
        }

        var source = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationTask = source.Task;
        return token.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            source);
    }

    private void LogEvaluation(
        string phase,
        RegisteredLookupGraceDecision decision,
        RegisteredLookupGracePolicy policy,
        long phaseStart,
        long now,
        RegisteredLookupGraceDecisionReason reason)
    {
        var snapshot = decision.Snapshot;
        _log.LogInformation(
            "registered_lookup_grace_evaluation phase={Phase} decision={Decision} reason={Reason} baseTimeoutMs={BaseTimeoutMs} maxGraceMs={MaxGraceMs} recentProgressWindowMs={RecentProgressWindowMs} maxRemaining={MaxRemaining} planned={Planned} durableCompleted={DurableCompleted} attemptsStarted={AttemptsStarted} inFlight={InFlight} finishedWithoutCheckpoint={FinishedWithoutCheckpoint} durableRemaining={DurableRemaining} lastDurableProgressAgeMs={LastDurableProgressAgeMs} observedMeanDurableIntervalMs={ObservedMeanDurableIntervalMs} maximumDurableGapMs={MaximumDurableGapMs} phaseElapsedMs={PhaseElapsedMs}",
            phase,
            decision.Granted ? "granted" : "denied",
            ToLogValue(reason),
            policy.BaseTimeout.TotalMilliseconds,
            policy.MaxGrace.TotalMilliseconds,
            policy.RecentProgressWindow.TotalMilliseconds,
            policy.MaxDurableRemaining,
            snapshot.Planned,
            snapshot.DurableCompleted,
            snapshot.AttemptsStarted,
            snapshot.InFlight,
            snapshot.FinishedWithoutCheckpoint,
            snapshot.DurableRemaining,
            decision.LastDurableProgressAge?.TotalMilliseconds,
            snapshot.ObservedMeanDurableIntervalMilliseconds,
            snapshot.MaximumDurableCompletionGap?.TotalMilliseconds,
            _timeProvider.GetElapsedTime(phaseStart, now).TotalMilliseconds);
    }

    private void LogTerminal(
        string phase,
        RegisteredLookupGraceTerminalReason reason,
        RegisteredLookupPassSnapshot snapshot,
        long grantTimestamp)
    {
        _log.LogInformation(
            "registered_lookup_grace_terminal phase={Phase} reason={Reason} planned={Planned} durableCompleted={DurableCompleted} attemptsStarted={AttemptsStarted} inFlight={InFlight} finishedWithoutCheckpoint={FinishedWithoutCheckpoint} durableRemaining={DurableRemaining} graceElapsedMs={GraceElapsedMs}",
            phase,
            ToLogValue(reason),
            snapshot.Planned,
            snapshot.DurableCompleted,
            snapshot.AttemptsStarted,
            snapshot.InFlight,
            snapshot.FinishedWithoutCheckpoint,
            snapshot.DurableRemaining,
            _timeProvider.GetElapsedTime(grantTimestamp).TotalMilliseconds);
    }

    private static TimeSpan Min(
        TimeSpan first,
        TimeSpan second) =>
        first <= second ? first : second;

    private static string ToLogValue(Enum value) =>
        string.Concat(
            value.ToString().SelectMany(
                static (character, index) =>
                    char.IsUpper(character) && index > 0
                        ? new[] { '_', char.ToLowerInvariant(character) }
                        : new[] { char.ToLowerInvariant(character) }));
}
