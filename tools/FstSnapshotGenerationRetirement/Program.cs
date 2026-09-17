using System.Text.Json;
using FstSnapshotGenerationRetirement;
using Npgsql;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    if (args.Length == 0
        || args[0] is "-h" or "--help" or "help")
    {
        PrintUsage();
        return 0;
    }

    try
    {
        VerifyBinaryPin();
        var command =
            RetirementCommandLine.Parse(args);
        using var cancellation =
            new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        await using var database =
            RetirementDatabase.FromEnvironment();
        var controller = new RetirementController(
            database,
            new RetirementRuntimeIdentityProvider());
        object result = command.Command switch
        {
            "status" =>
                await controller.StatusAsync(
                    cancellation.Token),
            "authorize-policy-epoch" =>
                await controller.AuthorizePolicyEpochAsync(
                    command.GetAuthorizationRequest(),
                    cancellation.Token),
            "reconcile" =>
                await controller.ReconcileAsync(
                    cancellation.Token),
            "deactivate-policy-epoch" =>
                await controller
                    .DeactivatePolicyEpochAsync(
                        cancellation.Token),
            "plan-cycle" =>
                await controller.PlanCycleAsync(
                    cancellation.Token),
            _ => throw new ArgumentException(
                $"Unknown command: {command.Command}"),
        };
        Console.WriteLine(
            JsonSerializer.Serialize(
                result,
                RetirementJson.Output));
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return 130;
    }
    catch (PostgresException exception)
    {
        Console.Error.WriteLine(
            $"ERROR: PostgreSQL rejected the retirement operation (SQLSTATE {exception.SqlState}).");
        return 1;
    }
    catch (NpgsqlException)
    {
        Console.Error.WriteLine(
            "ERROR: PostgreSQL connection or protocol validation failed.");
        return 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"ERROR: {exception.Message}");
        return 1;
    }
}

static void VerifyBinaryPin()
{
    var expected =
        Environment.GetEnvironmentVariable(
            SnapshotGenerationRetirementContract
                .BinaryHashEnvironment);
    if (!RetirementJson.IsSha256(expected))
    {
        throw new InvalidOperationException(
            $"{SnapshotGenerationRetirementContract.BinaryHashEnvironment} must be a lowercase SHA-256.");
    }
    var configuredPath =
        Environment.GetEnvironmentVariable(
            SnapshotGenerationRetirementContract
                .BinaryPathEnvironment);
    var processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException(
            "The retirement executable path is unavailable.");
    if (string.IsNullOrWhiteSpace(configuredPath)
        || !string.Equals(
            Path.GetFullPath(configuredPath),
            Path.GetFullPath(processPath),
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"{SnapshotGenerationRetirementContract.BinaryPathEnvironment} must identify the running retirement executable.");
    }
    var actual = RetirementJson.Sha256File(
        processPath);
    if (!string.Equals(
            expected,
            actual,
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "The retirement supervisor binary differs from its approved SHA-256.");
    }
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        Snapshot-generation retirement plan control plane

        Required operator environment:
          FST_SNAPSHOT_RETIREMENT_CONNECTION_STRING
          FST_SNAPSHOT_RETIREMENT_BINARY_SHA256

        Wrapper-provided environment:
          FST_SNAPSHOT_RETIREMENT_BINARY_PATH

        Commands:
          status
          reconcile
          deactivate-policy-epoch
          plan-cycle
          authorize-policy-epoch
            --not-before <UTC round-trip timestamp>
            --expires-at <UTC round-trip timestamp>
            --max-jobs <1-32>
            --max-total-bytes <bounded positive bytes>
            --approved-by <identity>
            --reviewed-by <distinct identity>
            --approval-reference <review evidence>
            --expected-repository-commit <40-hex>
            --expected-repository-tree <40-hex>
            --expected-supervisor-binary-sha256 <sha256>
            --expected-supervisor-source-sha256 <sha256>
            --expected-wrapper-sha256 <sha256>
            --expected-control-schema-sha256 <sha256>
            --expected-source-identity-sha256 <sha256>

        This binary can only authorize, report, reconcile, and plan one
        largest-first candidate from accepted report-only evidence. It has
        no archive, relation, SQL, force, batch, detach, quarantine, DROP,
        delete, truncate, restore, or worker lifecycle command.
        """);
}
