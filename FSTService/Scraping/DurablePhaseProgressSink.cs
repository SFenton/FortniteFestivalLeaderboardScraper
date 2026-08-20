using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FSTService.Persistence;

namespace FSTService.Scraping;

public interface IPhaseProgressClock
{
    DateTime UtcNow { get; }
}

internal sealed class SystemPhaseProgressClock : IPhaseProgressClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed record PhaseProgressObservation(
    string? SubphaseId,
    string? UnitsKind,
    long? UnitsCompleted,
    long? UnitsTotal,
    bool? UnitsTotalFinal);

public sealed record SubphaseProgressObservation(
    string? SubphaseId,
    string Kind,
    string? UnitsKind = null,
    long? UnitsCompleted = null,
    long? UnitsTotal = null,
    bool UnitsTotalFinal = false,
    long? ResetSequence = null);

public sealed record PhaseEtaEstimate(
    double LowerSeconds,
    double UpperSeconds,
    string Confidence,
    int SampleCount,
    string ModelVersion);

public sealed record DurablePhaseProgressView(
    long ScrapeId,
    string OperationId,
    string PhaseId,
    string PhaseStatus,
    string? SubphaseId,
    string PlanVersion,
    int PhaseOrdinal,
    int? Attempt,
    string? UnitsKind,
    long? UnitsCompleted,
    long? UnitsTotal,
    bool UnitsTotalFinal,
    double? PhasePercent,
    string OverallPercentKind,
    double? OverallPercent,
    string? OverallModelVersion,
    double? EtaLowerSeconds,
    double? EtaUpperSeconds,
    string? EtaConfidence,
    int? EtaSampleCount,
    DateTime StartedAtUtc,
    DateTime LastProgressAtUtc,
    SubphaseProgressInfo? SubphaseProgress = null);

public static class PhaseEtaEstimator
{
    public const int MinimumSamples = 5;
    public const string ModelVersion = "historical-phase-duration.v1";
    private const double MaximumCoefficientOfVariation = 0.35;

    public static PhaseEtaEstimate? TryEstimate(
        IReadOnlyCollection<long> durationSamplesMs,
        double? phasePercent,
        double? previousUpperSeconds = null)
    {
        if (phasePercent is null or <= 0 or >= 100)
            return null;

        var samples = durationSamplesMs
            .Where(sample => sample > 0)
            .Order()
            .Select(sample => sample / 1000.0)
            .ToArray();
        if (samples.Length < MinimumSamples)
            return null;

        var mean = samples.Average();
        if (mean <= 0)
            return null;
        var variance = samples.Sum(sample => Math.Pow(sample - mean, 2)) / samples.Length;
        var coefficientOfVariation = Math.Sqrt(variance) / mean;
        if (!double.IsFinite(coefficientOfVariation)
            || coefficientOfVariation > MaximumCoefficientOfVariation)
        {
            return null;
        }

        var remainingFraction = 1 - Math.Clamp(phasePercent.Value, 0, 100) / 100.0;
        var lower = Percentile(samples, 0.25) * remainingFraction;
        var upper = Percentile(samples, 0.75) * remainingFraction;
        if (previousUpperSeconds.HasValue)
            upper = Math.Min(upper, previousUpperSeconds.Value);
        lower = Math.Min(lower, upper);

        return new PhaseEtaEstimate(
            Math.Round(Math.Max(0, lower), 0),
            Math.Round(Math.Max(0, upper), 0),
            samples.Length >= 10 && coefficientOfVariation <= 0.20
                ? "high"
                : "medium",
            samples.Length,
            ModelVersion);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var position = (sorted.Count - 1) * percentile;
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
            return sorted[lowerIndex];
        var weight = position - lowerIndex;
        return sorted[lowerIndex] + ((sorted[upperIndex] - sorted[lowerIndex]) * weight);
    }
}

public sealed class DurablePhaseProgressSink
{
    private static readonly TimeSpan ProgressWriteInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] ConfigIdentityKeys =
    [
        "Scraper:EnabledPhases",
        "Scraper:RunOnce",
        "Scraper:DegreeOfParallelism",
        "Scraper:InitialDop",
        "Scraper:MaxRequestsPerSecond",
        "Scraper:ExpectedProxyEndpointCount",
        "Scraper:ProxyActiveStandby",
        "Scraper:ProxyMaxRequestsPerSecondPerEndpoint",
        "Scraper:ProxyMaxConcurrentRequestsPerEndpoint",
        "Scraper:ProxyDisableConnectionReuse",
        "Scraper:ProxyUseCurlTransport",
        "Scraper:SequentialScrape",
        "Scraper:PageConcurrency",
        "Scraper:SongConcurrency",
        "Scraper:LookupBatchSize",
        "Scraper:SongMachineDop",
        "Scraper:LeaderboardWriteMode",
        "Scraper:BoundedChannelCapacity",
        "Scraper:OnlineWriteBatchPages",
        "Scraper:OnlineDbWriterConcurrency",
        "Scraper:BandExtractionParallelism",
        "Scraper:BandMembershipRebuildBatchSize",
        "Scraper:SoloProjectionCleanupMaxDegreeOfParallelism",
        "Scraper:RankHistorySnapshotMaxDegreeOfParallelism",
        "Scraper:RivalsMaxDegreeOfParallelism",
        "Scraper:LeaderboardRivalsMaxDegreeOfParallelism",
        "Scraper:RefreshCurrentSeasonSessions",
        "Features:EnforcePublicationCriticalPhases",
        "Features:EnforceScopeCompletenessManifests",
        "Features:RequireSuccessfulScrapeWriters",
        "Features:UseLeaderboardScopeFingerprints",
        "Features:WritePublishedScopeSources",
        "Features:SkipUnchangedPhysicalLeaderboardSnapshots",
        "Scraper:BandCurrentProjectionUseBatchedMemberStatsAggregation",
        "BandRankHistory:Mode",
        "BandRankHistory:WriteMode",
        "BandTeamRankings:WriteMode",
        "BandTeamRankings:MaxParallelBandTypes",
        "BandTeamRankings:OverlapRankHistorySnapshotsWithBandRankings",
        "ImprovementNotifications:Enabled",
        "ImprovementNotifications:Scope",
        "DatabaseMaintenance:ServiceLevelRetentionMaintenanceEnabled",
        "DatabaseMaintenance:SnapshotRetentionReportOnlyWhenDisabled",
        "DatabaseMaintenance:SnapshotRetentionRewriteEnabled",
    ];

    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<DurablePhaseProgressSink> _log;
    private readonly IPhaseProgressClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveAttempt> _active = new(StringComparer.Ordinal);
    private readonly string _buildId;
    private readonly string _configId;
    private long? _scrapeId;
    private string? _workerInstanceId;

    public DurablePhaseProgressSink(
        IMetaDatabase metaDb,
        IConfiguration configuration,
        ILogger<DurablePhaseProgressSink> log,
        IPhaseProgressClock? clock = null)
    {
        _metaDb = metaDb;
        _log = log;
        _clock = clock ?? new SystemPhaseProgressClock();
        _buildId = typeof(DurablePhaseProgressSink).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(DurablePhaseProgressSink).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        _configId = ComputeConfigId(configuration);
    }

    public void AttachScrape(long scrapeId, string workerInstanceId)
    {
        if (scrapeId <= 0 || string.IsNullOrWhiteSpace(workerInstanceId))
            return;

        lock (_gate)
        {
            if (_scrapeId == scrapeId
                && string.Equals(
                    _workerInstanceId,
                    workerInstanceId,
                    StringComparison.Ordinal))
            {
                return;
            }
        }

        var now = _clock.UtcNow;
        TryPersistence(
            () => _metaDb.InterruptOrphanedScrapePhaseAttempts(
                workerInstanceId,
                now,
                "Worker instance changed before the phase reached a terminal state."),
            "interrupt orphaned phase attempts");

        lock (_gate)
        {
            _active.Clear();
            _scrapeId = scrapeId;
            _workerInstanceId = workerInstanceId;
        }
    }

    public DurablePhaseProgressView? StartPhase(
        PhaseProgressDescriptor descriptor,
        string? subphaseId = null)
    {
        if (descriptor.Reserved)
        {
            throw new InvalidOperationException(
                $"Reserved phase '{descriptor.Id}' cannot start an active progress attempt.");
        }

        long scrapeId;
        string workerInstanceId;
        lock (_gate)
        {
            if (!_scrapeId.HasValue || string.IsNullOrWhiteSpace(_workerInstanceId))
                return null;
            if (_active.TryGetValue(descriptor.Id, out var existing))
                return BuildView(existing);
            scrapeId = _scrapeId.Value;
            workerInstanceId = _workerInstanceId!;
        }

        var now = _clock.UtcNow;
        var samples = TryLoadSamples(descriptor.Id);
        int? attempt = null;
        TryPersistence(
            () => attempt = _metaDb.StartScrapePhaseAttempt(new ScrapePhaseAttemptStart(
                scrapeId,
                descriptor.Id,
                PhaseProgressCatalog.OperationId,
                descriptor.Ordinal,
                PhaseProgressCatalog.PlanVersion,
                workerInstanceId,
                subphaseId,
                "running",
                descriptor.DefaultUnitsKind,
                UnitsCompleted: null,
                UnitsTotal: null,
                UnitsTotalFinal: false,
                PhasePercent: null,
                OverallPercentKind: "indeterminate",
                OverallPercent: null,
                OverallModelVersion: null,
                EtaLowerSeconds: null,
                EtaUpperSeconds: null,
                EtaConfidence: null,
                EtaSampleCount: null,
                StartedAtUtc: now,
                LastProgressAtUtc: now,
                HeartbeatAtUtc: now,
                BuildId: _buildId,
                ConfigId: _configId,
                CurrentSubphaseEpoch: string.IsNullOrWhiteSpace(subphaseId) ? 0 : 1,
                SubphaseSequence: 0,
                SubphaseProgressKind: "indeterminate",
                SubphaseStartedAtUtc: string.IsNullOrWhiteSpace(subphaseId) ? null : now,
                SubphaseLastProgressAtUtc: string.IsNullOrWhiteSpace(subphaseId) ? null : now)),
            $"start phase {descriptor.Id}");

        var state = new ActiveAttempt(
            descriptor,
            scrapeId,
            attempt,
            now,
            workerInstanceId,
            subphaseId,
            descriptor.DefaultUnitsKind,
            samples);
        lock (_gate)
            _active[descriptor.Id] = state;
        return BuildView(state);
    }

    public DurablePhaseProgressView? TransitionSubphase(
        string phaseId,
        string? subphaseId)
    {
        ActiveAttempt? state;
        lock (_gate)
            _active.TryGetValue(phaseId, out state);
        return state is null
            ? null
            : Observe(state, new PhaseProgressObservation(
                subphaseId,
                UnitsKind: null,
                UnitsCompleted: null,
                UnitsTotal: null,
                UnitsTotalFinal: null),
                new SubphaseProgressObservation(
                    subphaseId,
                    "indeterminate"),
                force: true);
    }

    public IReadOnlyList<DurablePhaseProgressView> ObserveTracker(
        OperationSnapshot? snapshot)
    {
        if (snapshot is null)
            return [];

        var writes = new List<DurablePhaseProgressView>();
        ActiveAttempt[] active;
        lock (_gate)
            active = _active.Values.ToArray();

        if (snapshot.Branches is { Count: > 0 })
        {
            foreach (var branch in snapshot.Branches)
            {
                var state = active.FirstOrDefault(attempt =>
                    string.Equals(
                        attempt.Descriptor.BranchId,
                        branch.Id,
                        StringComparison.Ordinal));
                if (state is null)
                    continue;
                var branchSubphaseId = BranchSubphaseId(
                    state.Descriptor);
                var view = Observe(state, new PhaseProgressObservation(
                    branchSubphaseId,
                    state.Descriptor.DefaultUnitsKind,
                    branch.Completed,
                    branch.Total,
                    branch.Total.HasValue),
                    new SubphaseProgressObservation(
                        branchSubphaseId,
                        branch.Total.HasValue ? "exact" : "indeterminate",
                        state.Descriptor.DefaultUnitsKind,
                        branch.Completed,
                        branch.Total,
                        branch.Total.HasValue));
                if (view is not null)
                    writes.Add(view);
            }
        }

        var primary = active
            .Where(attempt => attempt.Descriptor.BranchId is null)
            .Where(attempt => string.Equals(
                attempt.Descriptor.TrackerOperation,
                snapshot.Operation,
                StringComparison.Ordinal))
            .OrderByDescending(attempt => attempt.StartedAtUtc)
            .FirstOrDefault();
        if (primary is not null)
        {
            var view = Observe(
                primary,
                BuildObservation(snapshot, primary.Descriptor),
                BuildSubphaseObservation(snapshot, primary.Descriptor));
            if (view is not null)
                writes.Add(view);
        }

        return writes;
    }

    private static string? BranchSubphaseId(
        PhaseProgressDescriptor descriptor) =>
        descriptor.Id switch
        {
            "post.rank_recompute" =>
                "enriching_parallel_rank_recompute",
            "post.first_seen_season" =>
                "enriching_parallel_tail",
            _ => null,
        };

    public DurablePhaseProgressView? CompletePhase(
        string phaseId,
        string status,
        string? warningMessage = null,
        string? errorMessage = null)
    {
        ActiveAttempt? state;
        ScrapePhaseAttemptProgress? pendingProgress = null;
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (!_active.Remove(phaseId, out state))
                return null;

            if (state.PendingProgressAtUtc.HasValue)
            {
                var eta = PhaseEtaEstimator.TryEstimate(
                    GetComparableDurationSamples(state),
                    state.PhasePercent,
                    state.EtaUpperSeconds);
                state.EtaLowerSeconds = eta?.LowerSeconds;
                state.EtaUpperSeconds = eta?.UpperSeconds;
                state.EtaConfidence = eta?.Confidence;
                state.EtaSampleCount = eta?.SampleCount;
                state.LastProgressAtUtc = state.PendingProgressAtUtc.Value;
                state.LastPersistedAtUtc = now;
                state.PendingProgressAtUtc = null;
                if (state.Attempt.HasValue)
                {
                    pendingProgress = BuildProgressUpdate(
                        state,
                        now);
                }
            }
        }

        if (pendingProgress is not null)
        {
            TryPersistence(
                () => _metaDb.UpdateScrapePhaseAttemptProgress(
                    pendingProgress),
                $"update phase {state.Descriptor.Id}");
        }

        state.Status = NormalizeTerminalStatus(status);
        state.LastProgressAtUtc = now;
        if (state.Attempt.HasValue)
        {
            TryPersistence(
                () => _metaDb.CompleteScrapePhaseAttempt(new ScrapePhaseAttemptCompletion(
                    state.ScrapeId,
                    state.Descriptor.Id,
                    state.Attempt.Value,
                    state.Status,
                    now,
                    now,
                    now,
                    warningMessage,
                    errorMessage)),
                $"complete phase {phaseId}");
        }
        return BuildView(state);
    }

    public void Heartbeat()
    {
        long? scrapeId;
        string? instanceId;
        lock (_gate)
        {
            scrapeId = _scrapeId;
            instanceId = _workerInstanceId;
        }
        if (!scrapeId.HasValue || string.IsNullOrWhiteSpace(instanceId))
            return;

        var now = _clock.UtcNow;
        TryPersistence(
            () => _metaDb.HeartbeatScrapePhaseAttempts(
                scrapeId.Value,
                instanceId!,
                now),
            "heartbeat phase attempts");
    }

    public void EndScrape(string? detail = null)
    {
        string[] activeIds;
        lock (_gate)
            activeIds = _active.Keys.ToArray();
        foreach (var phaseId in activeIds)
        {
            CompletePhase(
                phaseId,
                "interrupted",
                warningMessage: detail ?? "Scrape pass ended before the phase emitted a terminal transition.");
        }

        lock (_gate)
        {
            _scrapeId = null;
            _workerInstanceId = null;
        }
    }

    private DurablePhaseProgressView? Observe(
        ActiveAttempt state,
        PhaseProgressObservation observation,
        SubphaseProgressObservation? subphaseObservation = null,
        bool force = false)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (!_active.TryGetValue(state.Descriptor.Id, out var current)
                || !ReferenceEquals(current, state))
            {
                return null;
            }

            var normalizedCompleted = observation.UnitsCompleted.HasValue
                ? Math.Max(state.UnitsCompleted ?? 0, observation.UnitsCompleted.Value)
                : state.UnitsCompleted;
            var normalizedUnitsKind = observation.UnitsKind
                ?? state.UnitsKind;
            var normalizedTotal = observation.UnitsTotal ?? state.UnitsTotal;
            var normalizedTotalFinal = observation.UnitsTotalFinal
                ?? state.UnitsTotalFinal;
            double? exactPercent = normalizedTotalFinal
                && normalizedTotal is > 0
                && normalizedCompleted.HasValue
                    ? Math.Round(Math.Clamp(
                        (double)normalizedCompleted.Value / normalizedTotal.GetValueOrDefault() * 100.0,
                        0,
                        100), 1)
                    : null;
            if (state.PhasePercent.HasValue && exactPercent.HasValue)
                exactPercent = Math.Max(state.PhasePercent.Value, exactPercent.Value);

            var subphaseIdChanged = !string.Equals(
                state.SubphaseId,
                observation.SubphaseId,
                StringComparison.Ordinal);
            var resetSequenceChanged = !subphaseIdChanged
                && subphaseObservation?.ResetSequence is not null
                && state.SubphaseResetSequence is not null
                && state.SubphaseResetSequence
                    != subphaseObservation.ResetSequence;
            if (!subphaseIdChanged
                && state.SubphaseResetSequence is null
                && subphaseObservation?.ResetSequence is not null)
            {
                state.SubphaseResetSequence =
                    subphaseObservation.ResetSequence;
            }
            var subphaseChanged =
                subphaseIdChanged || resetSequenceChanged;
            var phaseMeaningful =
                subphaseChanged
                || !string.Equals(state.UnitsKind, normalizedUnitsKind, StringComparison.Ordinal)
                || normalizedCompleted != state.UnitsCompleted
                || normalizedTotal != state.UnitsTotal
                || normalizedTotalFinal != state.UnitsTotalFinal
                || exactPercent != state.PhasePercent;
            var subphaseMeaningful = false;

            if (subphaseChanged)
            {
                state.SubphaseId = observation.SubphaseId;
                state.SubphaseResetSequence =
                    subphaseObservation?.ResetSequence;
                state.SubphaseEpoch++;
                state.SubphaseSequence++;
                state.SubphaseProgressKind = "indeterminate";
                state.SubphaseUnitsKind = null;
                state.SubphaseUnitsCompleted = null;
                state.SubphaseUnitsTotal = null;
                state.SubphaseUnitsTotalFinal = false;
                state.SubphasePercent = null;
                state.SubphaseStartedAtUtc = now;
                state.SubphaseLastProgressAtUtc = now;
                subphaseMeaningful = true;
            }
            if (subphaseObservation is not null)
            {
                var subphaseKind = NormalizeSubphaseKind(
                    subphaseObservation.Kind);
                var subphaseUnitsKind = subphaseKind == "exact"
                    ? subphaseObservation.UnitsKind
                    : null;
                var subphaseUnitsTotal = subphaseKind == "exact"
                    ? subphaseObservation.UnitsTotal
                    : null;
                var subphaseUnitsCompleted = subphaseKind == "exact"
                    ? subphaseObservation.UnitsCompleted
                    : null;
                var subphaseUnitsTotalFinal = subphaseKind == "exact"
                    && subphaseObservation.UnitsTotalFinal;

                if (subphaseKind == "exact"
                    && (!subphaseUnitsTotalFinal
                        || subphaseUnitsTotal is not > 0
                        || subphaseUnitsCompleted is null
                        || subphaseUnitsCompleted < 0
                        || subphaseUnitsCompleted > subphaseUnitsTotal))
                {
                    subphaseKind = "indeterminate";
                    subphaseUnitsKind = null;
                    subphaseUnitsCompleted = null;
                    subphaseUnitsTotal = null;
                    subphaseUnitsTotalFinal = false;
                }

                if (subphaseKind == "exact"
                    && state.SubphaseProgressKind == "exact"
                    && subphaseUnitsCompleted.HasValue)
                {
                    subphaseUnitsCompleted = Math.Max(
                        state.SubphaseUnitsCompleted ?? 0,
                        subphaseUnitsCompleted.Value);
                }

                double? subphasePercent = subphaseKind == "exact"
                    ? Math.Round(
                        (double)subphaseUnitsCompleted!.Value
                        / subphaseUnitsTotal!.Value
                        * 100.0,
                        1)
                    : null;
                if (subphaseKind == "exact"
                    && state.SubphaseProgressKind == "exact"
                    && state.SubphasePercent.HasValue)
                {
                    subphasePercent = Math.Max(
                        state.SubphasePercent.Value,
                        subphasePercent!.Value);
                }

                var observationMeaningful =
                    !string.Equals(
                        state.SubphaseProgressKind,
                        subphaseKind,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        state.SubphaseUnitsKind,
                        subphaseUnitsKind,
                        StringComparison.Ordinal)
                    || state.SubphaseUnitsCompleted != subphaseUnitsCompleted
                    || state.SubphaseUnitsTotal != subphaseUnitsTotal
                    || state.SubphaseUnitsTotalFinal != subphaseUnitsTotalFinal
                    || state.SubphasePercent != subphasePercent;

                if (observationMeaningful)
                {
                    state.SubphaseProgressKind = subphaseKind;
                    state.SubphaseUnitsKind = subphaseUnitsKind;
                    state.SubphaseUnitsCompleted = subphaseUnitsCompleted;
                    state.SubphaseUnitsTotal = subphaseUnitsTotal;
                    state.SubphaseUnitsTotalFinal =
                        subphaseUnitsTotalFinal;
                    state.SubphasePercent = subphasePercent;
                    state.SubphaseStartedAtUtc ??= now;
                    state.SubphaseLastProgressAtUtc = now;
                }
                subphaseMeaningful |= observationMeaningful;
            }

            var meaningful = phaseMeaningful || subphaseMeaningful;
            var pendingFlushDue = state.PendingProgressAtUtc.HasValue
                && now - state.LastPersistedAtUtc >= ProgressWriteInterval;
            if (!meaningful && !pendingFlushDue)
                return null;

            if (phaseMeaningful)
            {
                state.UnitsKind = normalizedUnitsKind;
                state.UnitsCompleted = normalizedCompleted;
                state.UnitsTotal = normalizedTotal;
                state.UnitsTotalFinal = normalizedTotalFinal;
                state.PhasePercent = exactPercent;
            }
            if (meaningful)
            {
                if (!subphaseChanged)
                    state.SubphaseSequence++;
                state.PendingProgressAtUtc = now;
            }

            if (!force
                && !subphaseChanged
                && now - state.LastPersistedAtUtc < ProgressWriteInterval)
            {
                return null;
            }

            var eta = PhaseEtaEstimator.TryEstimate(
                GetComparableDurationSamples(state),
                state.PhasePercent,
                state.EtaUpperSeconds);
            state.EtaLowerSeconds = eta?.LowerSeconds;
            state.EtaUpperSeconds = eta?.UpperSeconds;
            state.EtaConfidence = eta?.Confidence;
            state.EtaSampleCount = eta?.SampleCount;
            state.LastProgressAtUtc = state.PendingProgressAtUtc ?? now;
            state.LastPersistedAtUtc = now;
            state.PendingProgressAtUtc = null;

            if (state.Attempt.HasValue)
            {
                TryPersistence(
                    () => _metaDb.UpdateScrapePhaseAttemptProgress(
                        BuildProgressUpdate(state, now)),
                    $"update phase {state.Descriptor.Id}");
            }
            return BuildView(state);
        }
    }

    private static ScrapePhaseAttemptProgress BuildProgressUpdate(
        ActiveAttempt state,
        DateTime heartbeatAtUtc) =>
        new(
            state.ScrapeId,
            state.Descriptor.Id,
            state.Attempt!.Value,
            state.SubphaseId,
            state.UnitsKind,
            state.UnitsCompleted,
            state.UnitsTotal,
            state.UnitsTotalFinal,
            state.PhasePercent,
            "indeterminate",
            OverallPercent: null,
            OverallModelVersion: null,
            state.EtaLowerSeconds,
            state.EtaUpperSeconds,
            state.EtaConfidence,
            state.EtaSampleCount,
            state.LastProgressAtUtc,
            heartbeatAtUtc,
            state.WorkerInstanceId,
            state.SubphaseEpoch,
            state.SubphaseSequence,
            state.SubphaseProgressKind,
            state.SubphaseUnitsKind,
            state.SubphaseUnitsCompleted,
            state.SubphaseUnitsTotal,
            state.SubphaseUnitsTotalFinal,
            state.SubphasePercent,
            state.SubphaseStartedAtUtc,
            state.SubphaseLastProgressAtUtc);

    private IReadOnlyList<PhaseDurationSample> TryLoadSamples(string phaseId)
    {
        IReadOnlyList<PhaseDurationSample> samples = [];
        TryPersistence(
            () => samples = _metaDb.GetSuccessfulPhaseDurationSamples(
                phaseId,
                PhaseProgressCatalog.PlanVersion,
                _configId,
                20),
            $"load ETA samples for {phaseId}");
        return samples;
    }

    private static IReadOnlyList<long> GetComparableDurationSamples(
        ActiveAttempt state)
    {
        if (!state.UnitsTotalFinal
            || string.IsNullOrWhiteSpace(state.UnitsKind)
            || state.UnitsTotal is not > 0)
        {
            return [];
        }

        var currentTotal = state.UnitsTotal.Value;
        return state.DurationSamples
            .Where(sample =>
                sample.UnitsTotalFinal
                && sample.UnitsTotal is > 0
                && string.Equals(
                    sample.UnitsKind,
                    state.UnitsKind,
                    StringComparison.Ordinal)
                && Math.Abs(sample.UnitsTotal.Value - currentTotal)
                    / (double)currentTotal <= 0.10)
            .Select(sample => sample.DurationMs)
            .ToArray();
    }

    private void TryPersistence(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Durable phase progress could not {Operation}. Scrape work will continue.",
                operation);
        }
    }

    private static PhaseProgressObservation BuildObservation(
        OperationSnapshot snapshot,
        PhaseProgressDescriptor descriptor)
    {
        if (snapshot.Leaderboards is not null)
        {
            return new PhaseProgressObservation(
                snapshot.SubOperation,
                "leaderboards",
                snapshot.Leaderboards.Completed,
                snapshot.Leaderboards.Total,
                UnitsTotalFinal: true);
        }
        if (snapshot.WorkItems is not null)
        {
            return new PhaseProgressObservation(
                snapshot.SubOperation,
                descriptor.DefaultUnitsKind ?? "items",
                snapshot.WorkItems.Completed,
                snapshot.WorkItems.Total,
                snapshot.WorkItemsTotalFinal == true);
        }
        if (snapshot.Accounts is not null)
        {
            return new PhaseProgressObservation(
                snapshot.SubOperation,
                "accounts",
                snapshot.Accounts.Completed,
                snapshot.Accounts.Total,
                UnitsTotalFinal: true);
        }
        if (snapshot.Batches is not null)
        {
            return new PhaseProgressObservation(
                snapshot.SubOperation,
                "batches",
                snapshot.Batches.Completed,
                snapshot.Batches.Total,
                UnitsTotalFinal: true);
        }
        if (snapshot.Branches is { Count: > 0 })
        {
            return new PhaseProgressObservation(
                snapshot.SubOperation,
                "branches",
                snapshot.Branches.Count(branch =>
                    branch.Status is "complete" or "skipped" or "failed"),
                snapshot.Branches.Count,
                UnitsTotalFinal: true);
        }
        return new PhaseProgressObservation(
            snapshot.SubOperation,
            descriptor.DefaultUnitsKind,
            UnitsCompleted: null,
            UnitsTotal: null,
            UnitsTotalFinal: false);
    }

    private static SubphaseProgressObservation BuildSubphaseObservation(
        OperationSnapshot snapshot,
        PhaseProgressDescriptor descriptor)
    {
        var id = snapshot.SubOperation;
        if (string.IsNullOrWhiteSpace(id))
        {
            return descriptor.Id switch
            {
                "post.account_name_resolution" when snapshot.Batches is not null =>
                    ExactSubphase(
                        null,
                        "batches",
                        snapshot.Batches.Completed,
                        snapshot.Batches.Total),
                "post.legacy_band_scrape" when snapshot.WorkItems is not null =>
                    ExactSubphase(
                        null,
                        descriptor.DefaultUnitsKind ?? "items",
                        snapshot.WorkItems.Completed,
                        snapshot.WorkItems.Total,
                        snapshot.WorkItemsTotalFinal == true),
                "post.leaderboard_rivals" when snapshot.WorkItems is not null =>
                    ExactSubphase(
                        null,
                        "accounts",
                        snapshot.WorkItems.Completed,
                        snapshot.WorkItems.Total,
                        snapshot.WorkItemsTotalFinal == true),
                "post.player_stats_tiers" when snapshot.WorkItems is not null =>
                    ExactSubphase(
                        null,
                        "accounts",
                        snapshot.WorkItems.Completed,
                        snapshot.WorkItems.Total,
                        snapshot.WorkItemsTotalFinal == true),
                _ => new SubphaseProgressObservation(
                    null,
                    "indeterminate"),
            };
        }

        if (id is "cancelling_band_after_solo_failure"
            or "skipping_band_after_timeout")
        {
            return new SubphaseProgressObservation(
                id,
                "not_applicable");
        }

        if (id == "fetching_leaderboards"
            && descriptor.Id == "scrape.leaderboards"
            && snapshot.Leaderboards is not null)
        {
            return ExactSubphase(
                id,
                "leaderboards",
                snapshot.Leaderboards.Completed,
                snapshot.Leaderboards.Total);
        }

        if (id == "deep_scraping"
            && descriptor.Id == "scrape.leaderboards"
            && snapshot.Detail?.DeepJobsTotal is > 0)
        {
            return ExactSubphase(
                id,
                "deep_jobs",
                snapshot.Detail.DeepJobsCompleted ?? 0,
                snapshot.Detail.DeepJobsTotal.Value);
        }

        if (id == "awaiting_band"
            && descriptor.Id == "scrape.leaderboards"
            && snapshot.Detail?.BandPagesTotal is > 0)
        {
            return ExactSubphase(
                id,
                "band_pages",
                snapshot.Detail.BandPagesCompleted ?? 0,
                snapshot.Detail.BandPagesTotal.Value) with
            {
                ResetSequence = snapshot.Detail.BandFetchEpoch,
            };
        }

        if ((id is "flushing_solo" or "flushing_band")
            && descriptor.Id == "scrape.leaderboards"
            && snapshot.Detail?.FlushPagesTotal is > 0)
        {
            return ExactSubphase(
                id,
                "pages",
                snapshot.Detail.FlushPagesCompleted ?? 0,
                snapshot.Detail.FlushPagesTotal.Value);
        }

        if (id == "draining_solo_writes"
            && descriptor.Id == "scrape.leaderboards"
            && snapshot.Detail?.OnlineWriterPagesTotal is > 0)
        {
            return ExactSubphase(
                id,
                "pages",
                snapshot.Detail.OnlineWriterPagesCompleted ?? 0,
                snapshot.Detail.OnlineWriterPagesTotal.Value);
        }

        if ((id is "dropping_solo_indexes"
                or "creating_solo_indexes"
                or "dropping_band_indexes")
            && descriptor.Id == "scrape.leaderboards"
            && snapshot.Detail?.IndexesTotal is > 0)
        {
            return ExactSubphase(
                id,
                "indexes",
                snapshot.Detail.IndexesCompleted ?? 0,
                snapshot.Detail.IndexesTotal.Value);
        }

        if (id == "creating_band_indexes"
            && descriptor.Id == "scrape.leaderboards")
        {
            return new SubphaseProgressObservation(
                id,
                "not_applicable");
        }

        if (id == "rank_history_snapshots"
            && descriptor.Id == "post.compute_rankings"
            && snapshot.Detail?.BandRankHistoryChunksTotal is > 0)
        {
            return ExactSubphase(
                id,
                "chunks",
                snapshot.Detail.BandRankHistoryChunksCompleted ?? 0,
                snapshot.Detail.BandRankHistoryChunksTotal.Value);
        }

        if (id is "processing_songs"
            && descriptor.Id == "post.refresh_registered_users"
            && snapshot.WorkItems is not null)
        {
            return ExactSubphase(
                id,
                descriptor.DefaultUnitsKind ?? "songs",
                snapshot.WorkItems.Completed,
                snapshot.WorkItems.Total,
                snapshot.WorkItemsTotalFinal == true);
        }

        if ((id is "extracting_band_context"
                or "rebuilding_band_membership_summary")
            && descriptor.Id == "post.band_extraction"
            && snapshot.WorkItems is not null)
        {
            return ExactSubphase(
                id,
                descriptor.DefaultUnitsKind ?? "items",
                snapshot.WorkItems.Completed,
                snapshot.WorkItems.Total,
                snapshot.WorkItemsTotalFinal == true);
        }

        if ((id is "registered_player_band_discovery"
                or "registered_band_targeted_processing")
            && (descriptor.Id is
                "post.registered_player_band_discovery"
                or "post.registered_band_targeted_processing")
            && snapshot.WorkItems is not null)
        {
            return ExactSubphase(
                id,
                descriptor.DefaultUnitsKind ?? "items",
                snapshot.WorkItems.Completed,
                snapshot.WorkItems.Total,
                snapshot.WorkItemsTotalFinal == true);
        }

        if (id == "per_song_rivals"
            && descriptor.Id == "post.rivals"
            && snapshot.Accounts is not null)
        {
            return ExactSubphase(
                id,
                "accounts",
                snapshot.Accounts.Completed,
                snapshot.Accounts.Total);
        }

        if (id == "population_tiers"
            && descriptor.Id == "post.player_stats_tiers"
            && snapshot.WorkItems is not null)
        {
            return ExactSubphase(
                id,
                "accounts",
                snapshot.WorkItems.Completed,
                snapshot.WorkItems.Total,
                snapshot.WorkItemsTotalFinal == true);
        }

        if (id == "activating_shadow_snapshots_early"
            && descriptor.Id == "post.activate_shadow_snapshots_early"
            && snapshot.WorkItems is not null)
        {
            return ExactSubphase(
                id,
                "steps",
                snapshot.WorkItems.Completed,
                snapshot.WorkItems.Total,
                snapshot.WorkItemsTotalFinal == true);
        }

        return new SubphaseProgressObservation(
            id,
            "indeterminate");
    }

    private static SubphaseProgressObservation ExactSubphase(
        string? id,
        string unitsKind,
        long completed,
        long total,
        bool totalFinal = true) =>
        new(
            id,
            totalFinal && total > 0 ? "exact" : "indeterminate",
            totalFinal && total > 0 ? unitsKind : null,
            totalFinal && total > 0 ? completed : null,
            totalFinal && total > 0 ? total : null,
            totalFinal && total > 0);

    private static DurablePhaseProgressView BuildView(ActiveAttempt state) =>
        new(
            state.ScrapeId,
            PhaseProgressCatalog.OperationId,
            state.Descriptor.Id,
            state.Status,
            state.SubphaseId,
            PhaseProgressCatalog.PlanVersion,
            state.Descriptor.Ordinal,
            state.Attempt,
            state.UnitsKind,
            state.UnitsCompleted,
            state.UnitsTotal,
            state.UnitsTotalFinal,
            state.PhasePercent,
            "indeterminate",
            OverallPercent: null,
            OverallModelVersion: null,
            state.EtaLowerSeconds,
            state.EtaUpperSeconds,
            state.EtaConfidence,
            state.EtaSampleCount,
            state.StartedAtUtc,
            state.LastProgressAtUtc,
            new SubphaseProgressInfo
            {
                Id = state.SubphaseId,
                Epoch = state.SubphaseEpoch,
                Sequence = state.SubphaseSequence,
                Kind = state.SubphaseProgressKind,
                UnitsKind = state.SubphaseUnitsKind,
                UnitsCompleted = state.SubphaseUnitsCompleted,
                UnitsTotal = state.SubphaseUnitsTotal,
                UnitsTotalFinal = state.SubphaseUnitsTotalFinal,
                Percent = state.SubphasePercent,
                StartedAtUtc = state.SubphaseStartedAtUtc,
                LastProgressAtUtc = state.SubphaseLastProgressAtUtc,
            });

    private static string NormalizeTerminalStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" or "complete" => "completed",
            "failed" => "failed",
            "cancelled" or "canceled" => "cancelled",
            "interrupted" => "interrupted",
            "skipped" => "skipped",
            "deferred" => "deferred",
            _ => "failed",
        };

    private static string NormalizeSubphaseKind(string kind) =>
        kind.ToLowerInvariant() switch
        {
            "exact" => "exact",
            "not_applicable" => "not_applicable",
            _ => "indeterminate",
        };

    private static string ComputeConfigId(IConfiguration configuration)
    {
        var text = string.Join(
            "\n",
            ConfigIdentityKeys.Select(key => $"{key}={configuration[key] ?? ""}"));
        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()}";
    }

    private sealed class ActiveAttempt
    {
        public ActiveAttempt(
            PhaseProgressDescriptor descriptor,
            long scrapeId,
            int? attempt,
            DateTime startedAtUtc,
            string workerInstanceId,
            string? subphaseId,
            string? unitsKind,
            IReadOnlyList<PhaseDurationSample> durationSamples)
        {
            Descriptor = descriptor;
            ScrapeId = scrapeId;
            Attempt = attempt;
            WorkerInstanceId = workerInstanceId;
            StartedAtUtc = startedAtUtc;
            LastProgressAtUtc = startedAtUtc;
            LastPersistedAtUtc = startedAtUtc;
            SubphaseId = subphaseId;
            SubphaseEpoch = string.IsNullOrWhiteSpace(subphaseId) ? 0 : 1;
            SubphaseProgressKind = "indeterminate";
            SubphaseStartedAtUtc = string.IsNullOrWhiteSpace(subphaseId)
                ? null
                : startedAtUtc;
            SubphaseLastProgressAtUtc = SubphaseStartedAtUtc;
            UnitsKind = unitsKind;
            DurationSamples = durationSamples;
        }

        public PhaseProgressDescriptor Descriptor { get; }
        public long ScrapeId { get; }
        public int? Attempt { get; }
        public string WorkerInstanceId { get; }
        public DateTime StartedAtUtc { get; }
        public DateTime LastProgressAtUtc { get; set; }
        public DateTime LastPersistedAtUtc { get; set; }
        public DateTime? PendingProgressAtUtc { get; set; }
        public string Status { get; set; } = "running";
        public string? SubphaseId { get; set; }
        public int SubphaseEpoch { get; set; }
        public long SubphaseSequence { get; set; }
        public long? SubphaseResetSequence { get; set; }
        public string SubphaseProgressKind { get; set; }
        public string? SubphaseUnitsKind { get; set; }
        public long? SubphaseUnitsCompleted { get; set; }
        public long? SubphaseUnitsTotal { get; set; }
        public bool SubphaseUnitsTotalFinal { get; set; }
        public double? SubphasePercent { get; set; }
        public DateTime? SubphaseStartedAtUtc { get; set; }
        public DateTime? SubphaseLastProgressAtUtc { get; set; }
        public string? UnitsKind { get; set; }
        public long? UnitsCompleted { get; set; }
        public long? UnitsTotal { get; set; }
        public bool UnitsTotalFinal { get; set; }
        public double? PhasePercent { get; set; }
        public double? EtaLowerSeconds { get; set; }
        public double? EtaUpperSeconds { get; set; }
        public string? EtaConfidence { get; set; }
        public int? EtaSampleCount { get; set; }
        public IReadOnlyList<PhaseDurationSample> DurationSamples { get; }
    }
}

public sealed class DurablePhaseProgressBridgeService : BackgroundService
{
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(1);
    private readonly ScrapeProgressTracker _tracker;
    private readonly DurablePhaseProgressSink _sink;
    private readonly WorkerStatusPublisher _workerStatus;

    public DurablePhaseProgressBridgeService(
        ScrapeProgressTracker tracker,
        DurablePhaseProgressSink sink,
        WorkerStatusPublisher workerStatus)
    {
        _tracker = tracker;
        _sink = sink;
        _workerStatus = workerStatus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var view in _sink.ObserveTracker(
                         _tracker.GetProgressResponse().Current))
            {
                _workerStatus.ApplyDurableProgress(view);
            }

            try
            {
                await Task.Delay(ObservationInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
