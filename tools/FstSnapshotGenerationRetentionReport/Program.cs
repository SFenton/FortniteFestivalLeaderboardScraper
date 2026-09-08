using System.Text.Json;
using FstSnapshotGenerationRetentionReport;
using FSTService.Persistence.Maintenance;
using Npgsql;

namespace FstSnapshotGenerationRetentionReport;

internal static class OfflineReportEntryPoint
{
public static async Task<int> Main(string[] args)
{
if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine("""
        Offline report-only snapshot-generation retention
        Commands:
          inspect
          observe-current
            --expected-repository-commit <40-hex>
            --expected-repository-tree <40-hex>
            --expected-source-sha256 <sha256>
            --expected-wrapper-sha256 <sha256>
            --expected-schema-sha256 <sha256>
            --expected-database-identity-sha256 <sha256>
            --expected-worker-configuration-sha256 <sha256>
        Environment:
          FST_SNAPSHOT_RETENTION_REPORT_CONNECTION_STRING
          FST_SNAPSHOT_RETENTION_REPORT_BINARY_SHA256
        No scrape, cycle, target, relation, SQL, path, lifecycle or destructive selector.
        """);
    return 0;
}

try
{
    OfflineReportCodeIdentityProvider.VerifyBinaryPin();
    var command = OfflineReportCommand.Parse(args);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(150));
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    var code = new OfflineReportCodeIdentityProvider();
    await using var database = OfflineReportDatabase.FromEnvironment();
    if (command.Name == "inspect")
    {
        var identity = await database.InspectAsync(code, cancellation.Token);
        await database.DisposeAsync();
        Write(new { outcome = "inspection_only", claim = OfflineReportContract.Claim, runtimeIdentity = identity });
        return 0;
    }

    var initialIdentity = await database.InspectAsync(code, cancellation.Token);
    command.Assertions!.Require(initialIdentity);
    var attestation = new OfflineReportAttestation(code, command.Assertions);
    var planner = database.CreatePlanner(initialIdentity.WorkerConfiguration!.ReportOnlyEnabled);
    var observation = await planner.ObserveCurrentOfflineAsync(attestation, cancellation.Token);
    observation = await database.CompleteAndDisposeAsync(observation);
    using var blockers = JsonDocument.Parse(observation.Cycle.GlobalBlockersJson);
    using var anomalies = JsonDocument.Parse(observation.Cycle.AnomaliesJson);
    var cycle = observation.Cycle;
    Write(new
    {
        claim = OfflineReportContract.Claim,
        disposition = observation.Result.Disposition.ToString(),
        status = cycle.Status,
        reportOnly = true,
        cycleId = cycle.CycleId,
        scrapeId = cycle.TriggerScrapeId,
        publicationId = cycle.TriggerPublicationId,
        cycle.SafePointKind,
        cycle.PlannerVersion,
        cycle.ConfigVersion,
        cycle.OracleAgreement,
        cycle.CandidateCount,
        cycle.ProtectedCount,
        cycle.BlockedCount,
        cycle.CandidateBytes,
        cycle.CandidateIdentityHash,
        cycle.ObservationHash,
        blockers = blockers.RootElement,
        anomalies = anomalies.RootElement,
        boundary = observation.Boundary,
        runtimeIdentity = attestation.ObservedIdentity,
        timings = observation.Timings,
        warnings = observation.Warnings,
    });
    return observation.Result.Disposition is SnapshotGenerationRetentionPlanDisposition.Observed
        or SnapshotGenerationRetentionPlanDisposition.Existing ? 0 : 2;
}
catch (OfflineReportRefusal exception)
{
    WriteError(exception.Code);
    return 2;
}
catch (SnapshotGenerationRetentionOfflineRefusal exception)
{
    Write(new
    {
        outcome = exception.Code is "observation_budget_exceeded" or "commit_outcome_uncertain"
            ? exception.Code : "rejected",
        claim = OfflineReportContract.Claim,
        code = exception.Code,
        phase = exception.Phase,
        elapsedMilliseconds = exception.ElapsedMilliseconds,
        timings = exception.Timings,
        possibleCommittedCycleId = exception.PossibleCommittedCycleId,
    });
    return 2;
}
catch (OperationCanceledException)
{
    WriteError("cancelled_or_deadline_exceeded");
    return 130;
}
catch (PostgresException exception)
{
    Write(new { outcome = "rejected", claim = OfflineReportContract.Claim,
        code = "postgres_rejected", sqlState = exception.SqlState });
    return 2;
}
catch (NpgsqlException)
{
    WriteError("connection_or_protocol_failed");
    return 2;
}
catch
{
    WriteError("offline_report_failed");
    return 2;
}
}

private static void Write<T>(T value) =>
    Console.WriteLine(JsonSerializer.Serialize(value, OfflineReportContract.Json));

private static void WriteError(string code) =>
    Write(new { outcome = "rejected", claim = OfflineReportContract.Claim, code });
}
