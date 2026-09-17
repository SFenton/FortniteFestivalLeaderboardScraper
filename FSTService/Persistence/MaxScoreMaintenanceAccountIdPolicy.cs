namespace FSTService.Persistence;

internal static class MaxScoreMaintenanceAccountIdPolicy
{
    internal static string FormatEvidenceId(
        string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return "sha256:"
               + Convert.ToHexStringLower(
                   System.Security.Cryptography.SHA256.HashData(
                       System.Text.Encoding.UTF8.GetBytes(
                           accountId)))[..16];
    }

    internal static string[] NormalizeSet(
        IEnumerable<string> accountIds)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        return accountIds
            .Where(static accountId =>
                !string.IsNullOrWhiteSpace(accountId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(
                static accountId => accountId,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                static accountId => accountId,
                StringComparer.Ordinal)
            .ToArray();
    }
}
