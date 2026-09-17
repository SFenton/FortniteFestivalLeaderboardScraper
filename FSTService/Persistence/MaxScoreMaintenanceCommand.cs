namespace FSTService.Persistence;

public enum MaxScoreMaintenanceAction
{
    Stage,
    Plan,
    Apply,
    Resume,
    Rollback,
}

public sealed record MaxScoreMaintenanceCommand(
    MaxScoreMaintenanceAction Action,
    long ExpectedPublishedScrapeId,
    string? StageRequestPath,
    IReadOnlyList<string> SongIds,
    string? ManifestPath,
    string? ManifestOutputPath,
    string ReportOutputPath,
    string? RollbackOutputPath,
    string? ExpectedManifestDigest,
    string? ExpectedPlanDigest,
    string? RollbackFilePath,
    string? ExpectedRollbackDigest,
    bool RollbackDryRun)
{
    public const string StageFlag = "--max-score-maintenance-stage";
    public const string PlanFlag = "--max-score-maintenance-plan";
    public const string ApplyFlag = "--max-score-maintenance-apply";
    public const string ResumeFlag = "--max-score-maintenance-resume";
    public const string RollbackFlag =
        "--max-score-maintenance-rollback";
    public const string StageRequestFlag =
        "--max-score-maintenance-stage-request";
    public const string SongIdFlag = "--max-score-maintenance-song-id";
    public const string ManifestFlag = "--max-score-maintenance-manifest";
    public const string ManifestOutputFlag =
        "--max-score-maintenance-manifest-output";
    public const string ReportOutputFlag =
        "--max-score-maintenance-report-output";
    public const string RollbackOutputFlag =
        "--max-score-maintenance-rollback-output";
    public const string RollbackFileFlag =
        "--max-score-maintenance-rollback-file";
    public const string ExpectedManifestDigestFlag =
        "--expected-max-score-manifest-digest";
    public const string ExpectedPlanDigestFlag =
        "--expected-max-score-plan-digest";
    public const string ExpectedRollbackDigestFlag =
        "--expected-max-score-rollback-digest";
    public const string RollbackDryRunFlag =
        "--max-score-maintenance-rollback-dry-run";
    public const string PublishedScrapeIdFlag =
        PublishedScrapeIdArgument.Flag;

    private static readonly HashSet<string> KnownFlags =
    [
        StageFlag,
        PlanFlag,
        ApplyFlag,
        ResumeFlag,
        RollbackFlag,
        StageRequestFlag,
        SongIdFlag,
        ManifestFlag,
        ManifestOutputFlag,
        ReportOutputFlag,
        RollbackOutputFlag,
        RollbackFileFlag,
        ExpectedManifestDigestFlag,
        ExpectedPlanDigestFlag,
        ExpectedRollbackDigestFlag,
        RollbackDryRunFlag,
        PublishedScrapeIdFlag,
    ];

    private static readonly HashSet<string> ActivationFlags =
        KnownFlags
            .Where(flag => !string.Equals(
                flag,
                PublishedScrapeIdFlag,
                StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static MaxScoreMaintenanceCommand? Parse(
        IReadOnlyList<string> args)
        => Parse(
            args,
            PublishedScrapeIdArgument.Parse(args));

    public static MaxScoreMaintenanceCommand? Parse(
        IReadOnlyList<string> args,
        PublishedScrapeIdArgument publishedScrapeId)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(publishedScrapeId);
        foreach (var argument in args)
        {
            var equals = argument.IndexOf('=');
            var option = equals >= 0
                ? argument[..equals]
                : argument;
            if ((option.StartsWith(
                     "--max-score-maintenance-",
                     StringComparison.OrdinalIgnoreCase)
                 || option.StartsWith(
                     "--expected-max-score-",
                     StringComparison.OrdinalIgnoreCase))
                && !KnownFlags.Contains(option))
            {
                throw new ArgumentException(
                    $"Unknown max-score maintenance option '{option}'.");
            }
        }

        var values = Tokenize(args);
        if (!values.Keys.Any(ActivationFlags.Contains))
            return null;

        var actions = new[]
        {
            (Flag: StageFlag, Action: MaxScoreMaintenanceAction.Stage),
            (Flag: PlanFlag, Action: MaxScoreMaintenanceAction.Plan),
            (Flag: ApplyFlag, Action: MaxScoreMaintenanceAction.Apply),
            (Flag: ResumeFlag, Action: MaxScoreMaintenanceAction.Resume),
            (Flag: RollbackFlag, Action: MaxScoreMaintenanceAction.Rollback),
        }
        .Where(action => values.ContainsKey(action.Flag))
        .ToArray();
        if (actions.Length != 1)
        {
            throw new ArgumentException(
                $"Specify exactly one of {StageFlag}, {PlanFlag}, {ApplyFlag}, {ResumeFlag}, or {RollbackFlag}.");
        }
        foreach (var actionFlag in new[]
                 {
                     StageFlag,
                     PlanFlag,
                     ApplyFlag,
                     ResumeFlag,
                     RollbackFlag,
                 })
        {
            RequireFlagWithoutValue(values, actionFlag);
        }

        var expectedPublishedScrapeId =
            publishedScrapeId.RequireValue(
                actions[0].Flag);
        var stageRequest = OptionalSingleValue(values, StageRequestFlag);
        var rawSongIds =
            values.TryGetValue(SongIdFlag, out var songValues)
                ? songValues
                    .Select(value => MaxScoreMaintenanceManifest
                        .NormalizeIdentifier(
                            value!,
                            SongIdFlag,
                            256))
                    .ToArray()
                : [];
        if (rawSongIds.Distinct(StringComparer.Ordinal).Count()
            != rawSongIds.Length)
        {
            throw new ArgumentException(
                $"{SongIdFlag} values must be unique.");
        }
        var songIds = rawSongIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (songIds.Length > MaxScoreMaintenanceManifest.MaximumSongs)
        {
            throw new ArgumentException(
                $"{SongIdFlag} supports at most {MaxScoreMaintenanceManifest.MaximumSongs} songs.");
        }

        var manifest = OptionalSingleValue(values, ManifestFlag);
        var manifestOutput = OptionalSingleValue(
            values,
            ManifestOutputFlag);
        var reportOutput = RequireSingleValue(
            values,
            ReportOutputFlag);
        var rollbackOutput = OptionalSingleValue(
            values,
            RollbackOutputFlag);
        var rollbackFile = OptionalSingleValue(
            values,
            RollbackFileFlag);
        var manifestDigest = NormalizeOptionalDigest(
            OptionalSingleValue(values, ExpectedManifestDigestFlag),
            ExpectedManifestDigestFlag);
        var planDigest = NormalizeOptionalDigest(
            OptionalSingleValue(values, ExpectedPlanDigestFlag),
            ExpectedPlanDigestFlag);
        var rollbackDigest = NormalizeOptionalDigest(
            OptionalSingleValue(
                values,
                ExpectedRollbackDigestFlag),
            ExpectedRollbackDigestFlag);
        var rollbackDryRun =
            RequireOptionalFlagWithoutValue(
                values,
                RollbackDryRunFlag);

        switch (actions[0].Action)
        {
            case MaxScoreMaintenanceAction.Stage:
                Require(stageRequest, StageRequestFlag);
                RejectSongs(songIds, StageFlag);
                Require(manifestOutput, ManifestOutputFlag);
                Reject(manifest, ManifestFlag, StageFlag);
                Reject(rollbackOutput, RollbackOutputFlag, StageFlag);
                Reject(manifestDigest, ExpectedManifestDigestFlag, StageFlag);
                Reject(planDigest, ExpectedPlanDigestFlag, StageFlag);
                Reject(rollbackFile, RollbackFileFlag, StageFlag);
                Reject(
                    rollbackDigest,
                    ExpectedRollbackDigestFlag,
                    StageFlag);
                RejectFlag(
                    rollbackDryRun,
                    RollbackDryRunFlag,
                    StageFlag);
                break;
            case MaxScoreMaintenanceAction.Plan:
                Require(manifest, ManifestFlag);
                Require(manifestDigest, ExpectedManifestDigestFlag);
                Reject(stageRequest, StageRequestFlag, PlanFlag);
                RejectSongs(songIds, PlanFlag);
                Reject(manifestOutput, ManifestOutputFlag, PlanFlag);
                Reject(rollbackOutput, RollbackOutputFlag, PlanFlag);
                Reject(planDigest, ExpectedPlanDigestFlag, PlanFlag);
                Reject(rollbackFile, RollbackFileFlag, PlanFlag);
                Reject(
                    rollbackDigest,
                    ExpectedRollbackDigestFlag,
                    PlanFlag);
                RejectFlag(
                    rollbackDryRun,
                    RollbackDryRunFlag,
                    PlanFlag);
                break;
            case MaxScoreMaintenanceAction.Apply:
                Require(manifest, ManifestFlag);
                Require(rollbackOutput, RollbackOutputFlag);
                Require(manifestDigest, ExpectedManifestDigestFlag);
                Require(planDigest, ExpectedPlanDigestFlag);
                Reject(stageRequest, StageRequestFlag, ApplyFlag);
                RejectSongs(songIds, ApplyFlag);
                Reject(manifestOutput, ManifestOutputFlag, ApplyFlag);
                Reject(rollbackFile, RollbackFileFlag, ApplyFlag);
                Reject(
                    rollbackDigest,
                    ExpectedRollbackDigestFlag,
                    ApplyFlag);
                RejectFlag(
                    rollbackDryRun,
                    RollbackDryRunFlag,
                    ApplyFlag);
                break;
            case MaxScoreMaintenanceAction.Resume:
                Require(manifest, ManifestFlag);
                Require(manifestDigest, ExpectedManifestDigestFlag);
                Require(planDigest, ExpectedPlanDigestFlag);
                Reject(stageRequest, StageRequestFlag, ResumeFlag);
                RejectSongs(songIds, ResumeFlag);
                Reject(manifestOutput, ManifestOutputFlag, ResumeFlag);
                Reject(rollbackFile, RollbackFileFlag, ResumeFlag);
                Reject(
                    rollbackDigest,
                    ExpectedRollbackDigestFlag,
                    ResumeFlag);
                RejectFlag(
                    rollbackDryRun,
                    RollbackDryRunFlag,
                    ResumeFlag);
                break;
            case MaxScoreMaintenanceAction.Rollback:
                Require(manifest, ManifestFlag);
                Require(manifestDigest, ExpectedManifestDigestFlag);
                Require(planDigest, ExpectedPlanDigestFlag);
                Require(rollbackFile, RollbackFileFlag);
                Require(
                    rollbackDigest,
                    ExpectedRollbackDigestFlag);
                Reject(stageRequest, StageRequestFlag, RollbackFlag);
                RejectSongs(songIds, RollbackFlag);
                Reject(
                    manifestOutput,
                    ManifestOutputFlag,
                    RollbackFlag);
                Reject(
                    rollbackOutput,
                    RollbackOutputFlag,
                    RollbackFlag);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return new MaxScoreMaintenanceCommand(
            actions[0].Action,
            expectedPublishedScrapeId,
            stageRequest,
            songIds,
            manifest,
            manifestOutput,
            reportOutput,
            rollbackOutput,
            manifestDigest,
            planDigest,
            rollbackFile,
            rollbackDigest,
            rollbackDryRun);
    }

    private static Dictionary<string, List<string?>> Tokenize(
        IReadOnlyList<string> args)
    {
        var result = new Dictionary<string, List<string?>>(
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            var equals = argument.IndexOf('=');
            var flag = equals >= 0 ? argument[..equals] : argument;
            if (!KnownFlags.Contains(flag))
                continue;

            string? value = equals >= 0
                ? argument[(equals + 1)..]
                : null;
            if (value is null
                && flag is not StageFlag
                    and not PlanFlag
                    and not ApplyFlag
                    and not ResumeFlag
                    and not RollbackFlag
                    and not RollbackDryRunFlag)
            {
                if (index + 1 >= args.Count
                    || args[index + 1].StartsWith(
                        "--",
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException($"{flag} requires a value.");
                }
                value = args[++index];
            }
            if (!result.TryGetValue(flag, out var list))
            {
                list = [];
                result[flag] = list;
            }
            list.Add(value);
        }

        return result;
    }

    private static void RequireFlagWithoutValue(
        IReadOnlyDictionary<string, List<string?>> values,
        string flag)
    {
        if (!values.TryGetValue(flag, out var occurrences))
            return;
        if (occurrences.Count != 1 || occurrences[0] is not null)
        {
            throw new ArgumentException(
                $"{flag} must be specified exactly once without a value.");
        }
    }

    private static bool RequireOptionalFlagWithoutValue(
            IReadOnlyDictionary<string, List<string?>> values,
            string flag)
    {
        if (!values.TryGetValue(flag, out var occurrences))
            return false;
        if (occurrences.Count != 1 || occurrences[0] is not null)
        {
            throw new ArgumentException(
                $"{flag} must be specified exactly once without a value.");
        }
        return true;
    }

    private static string RequireSingleValue(
        IReadOnlyDictionary<string, List<string?>> values,
        string flag)
        => OptionalSingleValue(values, flag)
            ?? throw new ArgumentException($"{flag} is required.");

    private static string? OptionalSingleValue(
        IReadOnlyDictionary<string, List<string?>> values,
        string flag)
    {
        if (!values.TryGetValue(flag, out var occurrences))
            return null;
        if (occurrences.Count != 1
            || string.IsNullOrWhiteSpace(occurrences[0]))
        {
            throw new ArgumentException(
                $"{flag} must be specified once with a nonblank value.");
        }
        return occurrences[0];
    }

    private static string? NormalizeOptionalDigest(
        string? value,
        string flag)
        => value is null
            ? null
            : MaxScoreMaintenanceManifest.NormalizeSha256(value, flag);

    private static void Require(string? value, string flag)
    {
        if (value is null)
            throw new ArgumentException($"{flag} is required.");
    }

    private static void Reject(
        string? value,
        string flag,
        string action)
    {
        if (value is not null)
            throw new ArgumentException($"{flag} is not valid with {action}.");
    }

    private static void RejectFlag(
        bool present,
        string flag,
        string action)
    {
        if (present)
            throw new ArgumentException($"{flag} is not valid with {action}.");
    }

    private static void RejectSongs(
        IReadOnlyCollection<string> songIds,
        string action)
    {
        if (songIds.Count > 0)
        {
            throw new ArgumentException(
                $"{SongIdFlag} is not valid with {action}.");
        }
    }
}
