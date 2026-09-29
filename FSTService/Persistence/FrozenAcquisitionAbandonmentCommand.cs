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

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(x => x.Equals(Flag, StringComparison.OrdinalIgnoreCase));

    public static FrozenAcquisitionAbandonmentCommand? Parse(IReadOnlyList<string> args)
    {
        if (!args.Any(x => x.StartsWith("--frozen-acquisition-", StringComparison.OrdinalIgnoreCase)))
            return null;
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
            translated.Add(arg.StartsWith("--frozen-acquisition-", StringComparison.OrdinalIgnoreCase)
                ? "--interrupted-acquisition-" + arg["--frozen-acquisition-".Length..]
                    .Replace("abandonment", "normalization", StringComparison.OrdinalIgnoreCase)
                : arg);
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
