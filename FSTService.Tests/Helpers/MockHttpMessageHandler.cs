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

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);

        if (_responses.Count == 0)
            throw new InvalidOperationException(
                $"MockHttpMessageHandler: No responses queued. Request: {request.Method} {request.RequestUri}");

        var entry = _responses.Dequeue();
        if (entry is Exception ex)
            throw ex;
        return Task.FromResult((HttpResponseMessage)entry);
    }
}
