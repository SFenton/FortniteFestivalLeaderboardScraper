namespace FSTService.Scraping;

public sealed record WriterFailedScope(
    string SongId,
    int PageCount,
    long RowCount);

public sealed record WriterBatchFailure(
    string WriterKind,
    string Instrument,
    IReadOnlyList<WriterFailedScope> Scopes,
    string ExceptionType,
    string ErrorMessage,
    string? ArtifactPath,
    DateTime OccurredAtUtc)
{
    public int PageCount => Scopes.Sum(static scope => scope.PageCount);
    public long RowCount => Scopes.Sum(static scope => scope.RowCount);
}

public sealed record WriterDrainResult(
    string WriterKind,
    long EnqueuedPages,
    long EnqueuedRows,
    long FlushedPages,
    long FlushedRows,
    IReadOnlyList<WriterBatchFailure> Failures,
    string? ReplayArtifactDirectory)
{
    public bool IsSuccess =>
        Failures.Count == 0
        && FlushedPages == EnqueuedPages
        && FlushedRows == EnqueuedRows;

    public static WriterDrainResult Empty(string writerKind) =>
        new(writerKind, 0, 0, 0, 0, [], null);
}

public sealed class ScrapeWriterException : Exception
{
    public ScrapeWriterException(long scrapeId, IReadOnlyList<WriterDrainResult> results)
        : base(BuildMessage(scrapeId, results))
    {
        ScrapeId = scrapeId;
        Results = results;
    }

    public long ScrapeId { get; }
    public IReadOnlyList<WriterDrainResult> Results { get; }

    private static string BuildMessage(
        long scrapeId,
        IReadOnlyList<WriterDrainResult> results)
    {
        var failures = results.SelectMany(static result => result.Failures).ToArray();
        return $"Scrape {scrapeId} has {failures.Length} writer failure batch(es), " +
               $"{failures.Sum(static failure => failure.PageCount)} page(s), and " +
               $"{failures.Sum(static failure => failure.RowCount)} row(s) retained for replay.";
    }
}
