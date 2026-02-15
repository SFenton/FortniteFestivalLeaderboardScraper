using System.Threading.Channels;

namespace FSTService.Scraping;

/// <summary>
/// A backfill request enqueued when a user logs in / registers.
/// </summary>
public sealed record BackfillRequest(string AccountId);

/// <summary>
/// Thread-safe queue for backfill requests.  Shared between:
/// <list type="bullet">
///   <item><see cref="FSTService.Auth.UserAuthService"/> — enqueues on login</item>
///   <item><see cref="FSTService.ScraperWorker"/> — drains after each scrape pass</item>
/// </list>
/// </summary>
public sealed class BackfillQueue
{
    private readonly Channel<BackfillRequest> _channel =
        Channel.CreateUnbounded<BackfillRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Enqueue a backfill request (non-blocking, never fails).</summary>
    public void Enqueue(BackfillRequest request) =>
        _channel.Writer.TryWrite(request);

    /// <summary>
    /// Drain all currently queued requests. Does not wait for new ones.
    /// </summary>
    public List<BackfillRequest> DrainAll()
    {
        var list = new List<BackfillRequest>();
        while (_channel.Reader.TryRead(out var req))
            list.Add(req);
        return list;
    }

    /// <summary>
    /// Whether there are any pending requests.
    /// </summary>
    public bool HasPending => _channel.Reader.TryPeek(out _);
}
