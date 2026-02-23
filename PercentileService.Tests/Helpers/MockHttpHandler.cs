using System.Net;

namespace PercentileService.Tests.Helpers;

/// <summary>
/// A delegating handler that uses a <see cref="Func{T, TResult}"/> to produce responses,
/// useful for unit-testing <see cref="HttpClient"/>-based services.
/// </summary>
public sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

    public MockHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// Create a handler that always returns the same response.
    /// </summary>
    public MockHttpHandler(HttpResponseMessage response)
        : this(_ => Task.FromResult(response)) { }

    /// <summary>
    /// Create a handler that returns 200 OK with the given JSON body.
    /// </summary>
    public static MockHttpHandler WithJsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new MockHttpHandler(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        }));
    }

    /// <summary>
    /// Ordered response queue — returns responses in order, then repeats the last one.
    /// </summary>
    public static MockHttpHandler WithSequence(params HttpResponseMessage[] responses)
    {
        int index = 0;
        return new MockHttpHandler(_ =>
        {
            var resp = responses[Math.Min(index, responses.Length - 1)];
            index++;
            return Task.FromResult(resp);
        });
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _handler(request);
    }
}
