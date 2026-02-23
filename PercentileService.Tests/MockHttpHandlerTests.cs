using System.Net;
using PercentileService.Tests.Helpers;

namespace PercentileService.Tests;

public sealed class MockHttpHandlerTests
{
    [Fact]
    public async Task WithJsonResponse_returns_configured_body()
    {
        var handler = MockHttpHandler.WithJsonResponse("""{"key":"value"}""");
        using var client = new HttpClient(handler);

        var resp = await client.GetAsync("http://test.example/api");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("key", body);
    }

    [Fact]
    public async Task WithJsonResponse_returns_configured_status()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}", HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);

        var resp = await client.GetAsync("http://test.example/api");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task WithSequence_returns_responses_in_order()
    {
        var handler = MockHttpHandler.WithSequence(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("first") },
            new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("second") },
            new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("third") });

        using var client = new HttpClient(handler);

        var r1 = await client.GetAsync("http://test.example/1");
        var r2 = await client.GetAsync("http://test.example/2");
        var r3 = await client.GetAsync("http://test.example/3");
        var r4 = await client.GetAsync("http://test.example/4"); // Repeats last

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, r3.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, r4.StatusCode); // Repeats last
    }

    [Fact]
    public async Task Requests_are_tracked()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        using var client = new HttpClient(handler);

        await client.GetAsync("http://test.example/a");
        await client.GetAsync("http://test.example/b");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/a", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("/b", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Constructor_with_static_response()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("ok"),
        };
        var handler = new MockHttpHandler(resp);
        using var client = new HttpClient(handler);

        var result = await client.GetAsync("http://test.example/x");

        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
    }
}
