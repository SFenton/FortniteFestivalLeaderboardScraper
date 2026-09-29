namespace FSTService.Persistence;

/// <summary>Explicit operator handoff for one stopped, frozen, uncheckpointed acquisition.</summary>
public sealed record FrozenAcquisitionAbandonmentCommand(
    InterruptedAcquisitionNormalizationCommand Identity,
    string ExpectedAttemptWorkerInstanceId,
    string FailureMessage)
{
    public const string Flag = "--frozen-acquisition-abandonment";
    public const string AttemptWorkerFlag = "--frozen-acquisition-attempt-worker-instance-id";
    public const string MessageFlag = "--frozen-acquisition-failure-message";
    private static readonly IReadOnlyDictionary<string, string> IdentityFlags =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--frozen-acquisition-scrape-id"] = InterruptedAcquisitionNormalizationCommand.ScrapeIdFlag,
            ["--frozen-acquisition-published-scrape-id"] = InterruptedAcquisitionNormalizationCommand.PublishedScrapeIdFlag,
            ["--frozen-acquisition-current-publication-id"] = InterruptedAcquisitionNormalizationCommand.CurrentPublicationIdFlag,
            ["--frozen-acquisition-previous-publication-id"] = InterruptedAcquisitionNormalizationCommand.PreviousPublicationIdFlag,
            ["--frozen-acquisition-working-publication-id"] = InterruptedAcquisitionNormalizationCommand.WorkingPublicationIdFlag,
            ["--frozen-acquisition-worker-instance-id"] = InterruptedAcquisitionNormalizationCommand.WorkerInstanceIdFlag,
            ["--frozen-acquisition-worker-freshness-utc"] = InterruptedAcquisitionNormalizationCommand.WorkerFreshnessFlag,
            ["--frozen-acquisition-phase-id"] = InterruptedAcquisitionNormalizationCommand.PhaseIdFlag,
            ["--frozen-acquisition-attempt"] = InterruptedAcquisitionNormalizationCommand.AttemptFlag,
        };

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(x => x.Equals(Flag, StringComparison.OrdinalIgnoreCase));

    public static FrozenAcquisitionAbandonmentCommand? Parse(IReadOnlyList<string> args)
    {
        if (!args.Any(x => x.StartsWith("--frozen-acquisition-", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (!IsRequested(args))
            throw new ArgumentException($"{Flag} is required.");
        var translated = new List<string>();
        string? attemptWorker = null, message = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var flag = arg.Split('=', 2)[0];
            if (flag.Equals(AttemptWorkerFlag, StringComparison.OrdinalIgnoreCase)
                || flag.Equals(MessageFlag, StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..]
                    : ++i < args.Count && !args[i].StartsWith("--", StringComparison.Ordinal)
                        ? args[i] : throw new ArgumentException($"Missing value for {flag}.");
                if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
                    throw new ArgumentException($"Invalid value for {flag}.");
                if (flag.Equals(AttemptWorkerFlag, StringComparison.OrdinalIgnoreCase))
                {
                    if (attemptWorker is not null) throw new ArgumentException("Duplicate attempt worker identity.");
                    attemptWorker = value;
                }
                else
                {
                    if (message is not null) throw new ArgumentException("Duplicate failure message.");
                    message = value;
                }
                continue;
            }
            var translatedFlag = flag.ToLowerInvariant() switch
            {
                Flag => InterruptedAcquisitionNormalizationCommand.MaintenanceFlag,
                "--frozen-acquisition-abandonment-check" => InterruptedAcquisitionNormalizationCommand.CheckFlag,
                "--frozen-acquisition-abandonment-execute" => InterruptedAcquisitionNormalizationCommand.ExecuteFlag,
                _ when IdentityFlags.TryGetValue(flag, out var identityFlag) => identityFlag,
                _ => flag,
            };
            translated.Add(translatedFlag + arg[flag.Length..]);
        }
        var identity = InterruptedAcquisitionNormalizationCommand.Parse(translated)
            ?? throw new ArgumentException("Missing frozen acquisition identity.");
        return new(identity,
            attemptWorker ?? throw new ArgumentException($"{AttemptWorkerFlag} is required."),
            message ?? throw new ArgumentException($"{MessageFlag} is required."));
    }
}

public sealed record FrozenAcquisitionAbandonmentReadiness(
    bool CanExecute,
    string? BlockingReason,
    FrozenAcquisitionAbandonmentCommand Request);

public sealed record FrozenAcquisitionAbandonmentExecutionResult(
    bool Succeeded,
    FrozenAcquisitionAbandonmentReadiness Before,
    FrozenAcquisitionAbandonmentReadiness? MutationReadiness,
    ActiveScrapeFailureIsolationReadiness? After,
    string? Error);
