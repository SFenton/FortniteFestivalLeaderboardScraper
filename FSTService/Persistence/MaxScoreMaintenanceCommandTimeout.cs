using Npgsql;

namespace FSTService.Persistence;

internal static class MaxScoreMaintenanceCommandTimeout
{
    internal static void Configure(
        NpgsqlCommand command,
        int commandTimeoutSeconds,
        string evidenceStage,
        Action<string, int>? configured = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceStage);
        if (!ScraperOptions.IsValidMaxScoreMaintenanceCommandTimeout(
                commandTimeoutSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeoutSeconds),
                commandTimeoutSeconds,
                "Max-score maintenance command timeout is outside the validated range.");
        }

        command.CommandTimeout = commandTimeoutSeconds;
        configured?.Invoke(evidenceStage, commandTimeoutSeconds);
    }
}
