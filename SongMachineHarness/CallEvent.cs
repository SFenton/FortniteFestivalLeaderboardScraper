namespace SongMachineHarness;

public sealed record CallEvent
{
    public required int CallId { get; init; }
    public required string Type { get; init; }    // "alltime" or "seasonal"
    public required string SongId { get; init; }
    public required string Instrument { get; init; }
    public required int BatchSize { get; init; }
    public required long StartMs { get; init; }
    /// <summary>Season prefix for seasonal calls (e.g. "season013"). Null for alltime.</summary>
    public string? Season { get; init; }
    public long EndMs { get; set; }
    public long DurationMs { get; set; }
    public int ResultCount { get; set; }
    public bool Success { get; set; }
    /// <summary>Number of HTTP wire sends (pagination pages) within this single instrumented call.</summary>
    public int PaginationPages { get; set; }
    public int InFlightAtStart { get; init; }
    public int InFlightAtEnd { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
}

public sealed record DopSample(
    long TimestampMs,
    int InFlight,
    int CurrentDop,
    long TotalRequests,
    int IdleSlots);
