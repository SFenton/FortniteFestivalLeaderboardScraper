using FSTService.Persistence;

namespace FSTService.Scraping;

public sealed class AccountNameRefreshService
{
    public const int MaxAccountIdsPerRequest = 4;

    private readonly AccountNameResolver _resolver;
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<AccountNameRefreshService> _log;

    public AccountNameRefreshService(
        AccountNameResolver resolver,
        IMetaDatabase metaDb,
        ILogger<AccountNameRefreshService> log)
    {
        _resolver = resolver;
        _metaDb = metaDb;
        _log = log;
    }

    public async Task<AccountNameRefreshResult> RefreshAsync(
        IEnumerable<string> accountIds,
        CancellationToken ct = default)
    {
        var requestedIds = NormalizeAccountIds(accountIds).ToArray();
        if (requestedIds.Length == 0)
            return AccountNameRefreshResult.Empty;

        var lookup = await _resolver.LookupAccountNamesAsync(requestedIds, maxConcurrency: 1, ct);
        var fetchedNames = lookup.Accounts
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value!.Trim(), StringComparer.OrdinalIgnoreCase);

        if (fetchedNames.Count == 0)
        {
            if (lookup.Failed > 0)
                _log.LogInformation("Account name refresh failed for {Failed}/{Requested} requested accounts.", lookup.Failed, lookup.Requested);
            return new AccountNameRefreshResult(
                Changed: 0,
                Unchanged: 0,
                Failed: lookup.Failed,
                Missing: lookup.Missing + lookup.Accounts.Count - fetchedNames.Count,
                Names: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ChangedAccountIds: []);
        }

        var existingNames = _metaDb.GetDisplayNames(requestedIds);
        var changedNames = fetchedNames
            .Where(pair => !existingNames.TryGetValue(pair.Key, out var existing)
                || !string.Equals(existing, pair.Value, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        if (changedNames.Count > 0)
        {
            _metaDb.InsertAccountNames(changedNames.Select(pair => (pair.Key, (string?)pair.Value)).ToList());
            _log.LogInformation("Account name refresh updated {Changed} account display names.", changedNames.Count);
        }

        return new AccountNameRefreshResult(
            Changed: changedNames.Count,
            Unchanged: fetchedNames.Count - changedNames.Count,
            Failed: lookup.Failed,
            Missing: lookup.Missing + lookup.Accounts.Count - fetchedNames.Count,
            Names: changedNames,
            ChangedAccountIds: changedNames.Keys.ToArray());
    }

    public static IReadOnlyList<string> NormalizeAccountIds(IEnumerable<string>? accountIds) => accountIds is null
        ? []
        : accountIds
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed record AccountNameRefreshResult(
    int Changed,
    int Unchanged,
    int Failed,
    int Missing,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyList<string> ChangedAccountIds)
{
    public static AccountNameRefreshResult Empty { get; } = new(
        Changed: 0,
        Unchanged: 0,
        Failed: 0,
        Missing: 0,
        Names: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        ChangedAccountIds: []);
}