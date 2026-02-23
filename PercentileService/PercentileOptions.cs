namespace PercentileService;

/// <summary>
/// Configuration for the Percentile Service.
/// </summary>
public sealed class PercentileOptions
{
    public const string Section = "Percentile";

    /// <summary>Account ID to query percentiles for (SFentonX).</summary>
    public string AccountId { get; set; } = "195e93ef108143b2975ee46662d4d0e1";

    /// <summary>Path to the refresh token file.</summary>
    public string TokenPath { get; set; } = "data/percentile-auth.json";

    /// <summary>How often to refresh the Epic token to keep it alive.</summary>
    public TimeSpan TokenRefreshInterval { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Time of day (PST) to run the daily percentile scrape.
    /// Format: "HH:mm" (24-hour). Default: 03:30 (3:30 AM PST).
    /// </summary>
    public string ScrapeTimeOfDay { get; set; } = "03:30";

    /// <summary>IANA timezone for the scrape schedule. Default: America/Los_Angeles (PST/PDT).</summary>
    public string ScrapeTimeZone { get; set; } = "America/Los_Angeles";

    /// <summary>Base URL of the FSTService API.</summary>
    public string FstBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>API key for authenticated FSTService endpoints.</summary>
    public string FstApiKey { get; set; } = "";

    /// <summary>Max concurrent V1 API requests during scrape.</summary>
    public int DegreeOfParallelism { get; set; } = 8;

    /// <summary>Maximum DOP the adaptive limiter will scale up to. Default: 2048.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 2048;

    /// <summary>Starting DOP for the adaptive limiter. Default: 1024.</summary>
    public int StartingDegreeOfParallelism { get; set; } = 1024;

    /// <summary>Minimum DOP the adaptive limiter will scale down to. Default: 2.</summary>
    public int MinDegreeOfParallelism { get; set; } = 2;

    /// <summary>
    /// How many seconds to wait on startup before the first scrape cycle.
    /// Allows TokenRefreshWorker to complete initial auth. Default: 10.
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 10;
}
