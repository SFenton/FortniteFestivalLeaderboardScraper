namespace FSTService.Persistence;

/// <summary>
/// Persistence-neutral acquisition wire telemetry. Null is reserved for
/// unavailable legacy or incomplete evidence; a present record is exact.
/// </summary>
public readonly record struct ScrapeWireSendTelemetry(
    long TotalSends,
    long ProbeSends,
    long ProbeSuccesses,
    long StatusRetries,
    long NetworkErrors,
    long CdnBlocks);
