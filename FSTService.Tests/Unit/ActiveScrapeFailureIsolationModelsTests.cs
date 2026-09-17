using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class ActiveScrapeFailureIsolationModelsTests
{
    [Fact]
    public void BlockingReason_reports_structural_safety_gates()
    {
        var cases = new (string Name, Func<ActiveScrapeFailureIsolationReadiness, ActiveScrapeFailureIsolationReadiness> Mutate, string Expected)[]
        {
            (
                "published scrape identity",
                readiness => readiness with { PublishedScrapeId = 99 },
                "expected published scrape 1, found 99"),
            (
                "candidate identity",
                readiness => readiness with { ScrapeId = 1 },
                "the active candidate must differ from the preserved published scrape"),
            (
                "missing candidate",
                readiness => readiness with { CandidateStatus = null },
                "scrape 2 does not exist"),
            (
                "missing publication",
                readiness => readiness with { CandidatePublicationId = null },
                "scrape 2 has no publication generation"),
            (
                "published scope rows",
                readiness => readiness with { CandidatePublishedScopeRowCount = 1 },
                "scrape 2 owns 1 published-scope row(s)"),
            (
                "one active query",
                readiness => readiness with { ActiveWorkerQueryCount = 1 },
                "worker still owns 1 active database query"),
            (
                "multiple active queries",
                readiness => readiness with { ActiveWorkerQueryCount = 2 },
                "worker still owns 2 active database queries"),
            (
                "waiting lock",
                readiness => readiness with { WaitingLockCount = 1 },
                "1 waiting database lock(s) remain"),
            (
                "advisory lock",
                readiness => readiness with { AdvisoryLockCount = 1 },
                "1 advisory database lock(s) remain"),
            (
                "maintenance",
                readiness => readiness with { MaintenanceActivityPresent = true },
                "database maintenance progress remains active"),
            (
                "foreign phase attempt",
                readiness => readiness with { ForeignRunningPhaseAttemptCount = 1 },
                "1 running phase attempt(s) belong to another worker instance"),
            (
                "missing worker status",
                readiness => readiness with { WorkerStatus = null },
                "scraper worker status is not present"),
            (
                "missing worker identity",
                readiness => readiness with { WorkerInstanceId = null },
                "scraper worker instance identity is not present"),
            (
                "missing worker freshness",
                readiness => readiness with { WorkerUpdatedAtUtc = null },
                "scraper worker freshness timestamp is not present"),
        };

        foreach (var testCase in cases)
        {
            var readiness = testCase.Mutate(CreateTerminalizedReadiness());

            Assert.False(readiness.CanExecute, testCase.Name);
            Assert.Equal(testCase.Expected, readiness.BlockingReason);
        }
    }

    [Fact]
    public void BlockingReason_reports_frozen_candidate_mismatches()
    {
        var cases = new (Func<ActiveScrapeFailureIsolationReadiness, ActiveScrapeFailureIsolationReadiness> Mutate, string Expected)[]
        {
            (
                readiness => readiness with
                {
                    PublicReadsFrozen = true,
                    FrozenScrapeId = 2,
                    FreezeReason = MetaDatabase.ActiveScrapeFailureIsolationFreezeReason,
                    WorkingPublicationId = 2,
                    CandidatePublicationStatus = "building",
                },
                "expected frozen published scrape 1, found 2"),
            (
                readiness => readiness with
                {
                    PublicReadsFrozen = true,
                    FrozenScrapeId = 1,
                    FreezeReason = "wrong-freeze-reason",
                    WorkingPublicationId = 2,
                    CandidatePublicationStatus = "building",
                },
                $"expected freeze reason {MetaDatabase.ActiveScrapeFailureIsolationFreezeReason}, found wrong-freeze-reason"),
            (
                readiness => readiness with
                {
                    PublicReadsFrozen = true,
                    FrozenScrapeId = 1,
                    FreezeReason = MetaDatabase.ActiveScrapeFailureIsolationFreezeReason,
                    WorkingPublicationId = null,
                    CandidatePublicationStatus = "building",
                },
                "working publication is not present"),
            (
                readiness => readiness with
                {
                    PublicReadsFrozen = true,
                    FrozenScrapeId = 1,
                    FreezeReason = MetaDatabase.ActiveScrapeFailureIsolationFreezeReason,
                    WorkingPublicationId = 3,
                    CandidatePublicationStatus = "building",
                },
                "candidate publication 2 does not own working publication 3"),
            (
                readiness => readiness with
                {
                    PublicReadsFrozen = true,
                    FrozenScrapeId = 1,
                    FreezeReason = MetaDatabase.ActiveScrapeFailureIsolationFreezeReason,
                    WorkingPublicationId = 2,
                    CandidateStatus = "pending",
                    CandidatePublicationStatus = "building",
                },
                "expected scrape 2 status running or failed, found pending"),
        };

        foreach (var (mutate, expected) in cases)
            Assert.Equal(expected, mutate(CreateTerminalizedReadiness()).BlockingReason);
    }

    [Fact]
    public void BlockingReason_reports_unfrozen_acquisition_and_terminal_states()
    {
        var cases = new (Func<ActiveScrapeFailureIsolationReadiness, ActiveScrapeFailureIsolationReadiness> Mutate, string Expected)[]
        {
            (
                readiness => readiness with
                {
                    CandidateStatus = "running",
                    WorkingPublicationId = 2,
                    CandidatePublicationStatus = "building",
                    RunningPhaseAttemptCount = 1,
                },
                "1 running phase attempt(s) remain for acquisition failure isolation"),
            (
                readiness => readiness with
                {
                    CandidateStatus = "running",
                    WorkingPublicationId = 2,
                    CandidatePublicationStatus = "building",
                    FailedAcquisitionPhaseAttemptCount = 0,
                },
                "no failed acquisition phase attempt is present"),
            (
                readiness => readiness with
                {
                    CandidateStatus = "running",
                    WorkingPublicationId = 2,
                    CandidatePublicationStatus = "building",
                    FailedAcquisitionPhaseAttemptCount = 1,
                    AcquisitionCheckpointPresent = true,
                },
                "an acquisition checkpoint is present; guarded resume must be used"),
            (
                readiness => readiness with
                {
                    CandidateStatus = "running",
                    WorkingPublicationId = 2,
                    CandidatePublicationStatus = "building",
                    FailedAcquisitionPhaseAttemptCount = 1,
                    WorkerStatus = "running",
                },
                "acquisition failure isolation requires an offline worker with no current operation"),
            (
                readiness => readiness with
                {
                    CandidateStatus = "pending",
                    CandidatePublicationStatus = "building",
                },
                "expected terminal scrape 2 status failed, found pending"),
            (
                readiness => readiness with
                {
                    CandidateStatus = "failed",
                    FrozenScrapeId = 1,
                    CandidatePublicationStatus = "building",
                },
                "terminalized publication state retained stale freeze identity"),
            (
                readiness => readiness with
                {
                    CandidateStatus = "failed",
                    WorkingPublicationId = 2,
                    CandidatePublicationStatus = "building",
                },
                "terminalized publication state retained working publication 2"),
            (
                readiness => readiness with
                {
                    CandidateStatus = "failed",
                    CandidatePublicationStatus = "building",
                },
                "expected candidate publication status failed, found building"),
        };

        foreach (var (mutate, expected) in cases)
        {
            var readiness = mutate(CreateTerminalizedReadiness());

            Assert.False(readiness.CanExecute);
            Assert.Equal(expected, readiness.BlockingReason);
        }
    }

    [Fact]
    public void ScrapeAuthenticationException_preserves_message_and_inner_exception()
    {
        var inner = new InvalidOperationException("token expired");

        var withoutInner = new ScrapeAuthenticationException("unauthorized");
        var withInner = new ScrapeAuthenticationException("unauthorized", inner);

        Assert.Equal("unauthorized", withoutInner.Message);
        Assert.Equal("unauthorized", withInner.Message);
        Assert.Same(inner, withInner.InnerException);
    }

    private static ActiveScrapeFailureIsolationReadiness CreateTerminalizedReadiness() =>
        new(
            ScrapeId: 2,
            ExpectedPublishedScrapeId: 1,
            PublishedScrapeId: 1,
            CandidateStatus: "failed",
            PublicReadsFrozen: false,
            FrozenScrapeId: null,
            FreezeReason: null,
            WorkingPublicationId: null,
            CandidatePublicationId: 2,
            CandidatePublicationStatus: "failed",
            CandidatePublishedScopeRowCount: 0,
            ActiveWorkerQueryCount: 0,
            WaitingLockCount: 0,
            AdvisoryLockCount: 0,
            MaintenanceActivityPresent: false,
            RunningPhaseAttemptCount: 0,
            ForeignRunningPhaseAttemptCount: 0,
            WorkerStatus: "offline",
            WorkerInstanceId: "worker-1",
            WorkerUpdatedAtUtc: DateTime.UtcNow,
            WorkerCurrentOperationPresent: false,
            FailedAcquisitionPhaseAttemptCount: 0,
            AcquisitionCheckpointPresent: false,
            CandidateFailurePhase: "acquisition_checkpoint");
}
