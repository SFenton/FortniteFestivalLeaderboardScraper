namespace FSTService.Scraping;

public sealed record AccountNameLookupResult(
    IReadOnlyDictionary<string, string?> Accounts,
    int Requested,
    int Failed,
    int Missing);