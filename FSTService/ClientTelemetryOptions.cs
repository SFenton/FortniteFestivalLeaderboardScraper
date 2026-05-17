namespace FSTService;

/// <summary>
/// Controls debug-only client interaction telemetry ingestion.
/// </summary>
public sealed class ClientTelemetryOptions
{
    public const string Section = "ClientTelemetry";

    /// <summary>When false, the ingestion endpoint returns 404.</summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum accepted events in a single client batch.</summary>
    public int MaxEventsPerBatch { get; set; } = 50;

    /// <summary>Maximum accepted request content length in bytes.</summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;
}