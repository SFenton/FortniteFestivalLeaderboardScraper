using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace FSTService.Scraping;

internal enum RegisteredLookupOutcome
{
    Success,
    NotFound,
    InvalidLeaderboard,
    HttpFailure,
    TransportFailure,
    Cancelled,
}

internal sealed class EpicLeaderboardUnavailableException : Exception
{
    public const string ErrorCode =
        "com.epicgames.events.invalid_leaderboard";

    public EpicLeaderboardUnavailableException()
        : base("The requested Epic leaderboard is not currently available.")
    {
    }

    public static bool IsExactInvalidLeaderboard(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(
                    "errorCode",
                    out var errorCode)
                && errorCode.ValueKind == JsonValueKind.String
                && string.Equals(
                    errorCode.GetString(),
                    ErrorCode,
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal static class RegisteredLookupInstrumentation
{
    private static readonly Meter Meter =
        new("FSTService.RegisteredBandLookups");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>(
            "fst.registered_band.logical_lookup.duration",
            "ms",
            "End-to-end elapsed duration of one registered-band logical lookup.");

    public static Stopwatch Start() => Stopwatch.StartNew();

    public static void Record(
        Stopwatch stopwatch,
        string phase,
        RegisteredLookupOutcome outcome)
    {
        stopwatch.Stop();
        Duration.Record(
            stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>(
                "outcome",
                outcome.ToString().ToLowerInvariant()));
    }

    public static RegisteredLookupOutcome ClassifyFailure(Exception? exception)
        => exception switch
        {
            EpicLeaderboardUnavailableException =>
                RegisteredLookupOutcome.InvalidLeaderboard,
            HttpRequestException =>
                RegisteredLookupOutcome.HttpFailure,
            _ => RegisteredLookupOutcome.TransportFailure,
        };
}

internal interface IPartialResultFailure
{
    object PartialResult { get; }
}

internal sealed class PartialResultFailureException<T> : Exception,
    IPartialResultFailure
{
    public PartialResultFailureException(
        T partialResult,
        Exception innerException)
        : base(innerException.Message, innerException)
    {
        PartialResultValue = partialResult;
    }

    public T PartialResultValue { get; }
    object IPartialResultFailure.PartialResult => PartialResultValue!;
}

internal sealed class PartialResultOperationCanceledException<T>
    : OperationCanceledException
{
    public PartialResultOperationCanceledException(
        T partialResult,
        OperationCanceledException innerException)
        : base(innerException.Message, innerException, innerException.CancellationToken)
    {
        PartialResult = partialResult;
    }

    public T PartialResult { get; }
}
