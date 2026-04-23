using System.Net;

namespace FSTService.Tests.Helpers;

/// <summary>
/// A mock HttpMessageHandler that returns preconfigured responses.
/// Captures all requests for assertion.
/// </summary>
public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<object> _responses = new();   // HttpResponseMessage or Exception
    private readonly List<HttpRequestMessage> _requests = new();

    /// <summary>All requests sent through this handler.</summary>
    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    /// <summary>Enqueue a response to return for the next request.</summary>
    public void EnqueueResponse(HttpResponseMessage response)
    {
        _responses.Enqueue(response);
    }

    /// <summary>Enqueue a JSON response with the given status code.</summary>
    public void EnqueueJsonResponse(HttpStatusCode statusCode, string json)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        _responses.Enqueue(response);
    }

    /// <summary>Enqueue a simple OK response with JSON body.</summary>
    public void EnqueueJsonOk(string json) => EnqueueJsonResponse(HttpStatusCode.OK, json);

    /// <summary>Enqueue a simple error response.</summary>
    public void EnqueueError(HttpStatusCode statusCode, string body = "")
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body),
        };
        _responses.Enqueue(response);
    }

    /// <summary>Enqueue an exception to throw on the next request.</summary>
    public void EnqueueException(Exception exception) => _responses.Enqueue(exception);

    /// <summary>Enqueue a 429 response with a Retry-After header.</summary>
    public void Enqueue429(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        _responses.Enqueue(response);
    }

    /// <summary>Enqueue a CDN-style 403 response with HTML body (non-JSON).</summary>
    public void EnqueueHtml403()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "<html><head><title>403 Forbidden</title></head><body><center><h1>403 Forbidden</h1></center></body></html>",
                System.Text.Encoding.UTF8, "text/html"),
        };
        _responses.Enqueue(response);
    }

    /// <summary>Sentinel used to represent a "hang forever" response.</summary>
    private sealed class HangSentinel { public static readonly HangSentinel Instance = new(); }

    /// <summary>Enqueue a request that hangs until its CancellationToken fires.
    /// Simulates an infinite-timeout proxy-routed send that never returns on its own
    /// (the production CDN-probe wedge). The handler throws
    /// <see cref="OperationCanceledException"/> when the request's token cancels.</summary>
    public void EnqueueHang() => _responses.Enqueue(HangSentinel.Instance);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);

        if (_responses.Count == 0)
            throw new InvalidOperationException(
                $"MockHttpMessageHandler: No responses queued. Request: {request.Method} {request.RequestUri}");

        var entry = _responses.Dequeue();
        if (entry is Exception ex)
            throw ex;
        if (entry is HangSentinel)
        {
            // Block until the request's token cancels, then throw like a real
            // HttpClient would when its per-request CTS fires.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = cancellationToken.Register(() => tcs.TrySetResult(true));
            await tcs.Task.ConfigureAwait(false);
            throw new TaskCanceledException("simulated hang cancelled", innerException: null, cancellationToken);
        }
        return (HttpResponseMessage)entry;
    }
}
