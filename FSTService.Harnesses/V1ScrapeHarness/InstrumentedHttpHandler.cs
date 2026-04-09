using System.Diagnostics;

namespace V1ScrapeHarness;

/// <summary>
/// DelegatingHandler that measures per-request HTTP wire time.
/// Inserted between HttpClient and SocketsHttpHandler in the pipeline.
/// </summary>
public sealed class InstrumentedHttpHandler : DelegatingHandler
{
    private readonly TimingCollector _collector;
    private readonly Stopwatch _sw;

    public InstrumentedHttpHandler(TimingCollector collector, Stopwatch sw, HttpMessageHandler inner)
        : base(inner)
    {
        _collector = collector;
        _sw = sw;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var startMs = _sw.ElapsedMilliseconds;
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch
        {
            var failMs = _sw.ElapsedMilliseconds - startMs;
            _collector.RecordHttpTiming(new HttpTimingSample(
                TimestampMs: startMs,
                WireMs: failMs,
                StatusCode: 0,
                ResponseBytes: 0,
                Url: request.RequestUri?.PathAndQuery));
            throw;
        }

        var wireMs = _sw.ElapsedMilliseconds - startMs;
        long responseBytes = response.Content.Headers.ContentLength ?? 0;

        _collector.RecordHttpTiming(new HttpTimingSample(
            TimestampMs: startMs,
            WireMs: wireMs,
            StatusCode: (int)response.StatusCode,
            ResponseBytes: responseBytes,
            Url: request.RequestUri?.PathAndQuery));

        return response;
    }
}
