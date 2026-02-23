using FSTService.Api;

namespace FSTService.Tests.Unit;

public class AuthEndpointsTests
{
    // ─── TryExtractLoopbackReturnTo ─────────────────────────

    [Fact]
    public void TryExtractLoopbackReturnTo_ValidLocalhostUrl_ReturnsUrl()
    {
        var json = "{\"return_to\":\"http://localhost:8400/auth/callback\"}";
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        var result = AuthEndpoints.TryExtractLoopbackReturnTo(state);

        Assert.Equal("http://localhost:8400/auth/callback", result);
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_DifferentPort_ReturnsUrl()
    {
        var json = "{\"return_to\":\"http://localhost:9999/oauth\"}";
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        var result = AuthEndpoints.TryExtractLoopbackReturnTo(state);

        Assert.Equal("http://localhost:9999/oauth", result);
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_Null_ReturnsNull()
    {
        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo(null));
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_Empty_ReturnsNull()
    {
        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo(""));
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_ExternalHost_ReturnsNull()
    {
        var json = "{\"return_to\":\"https://evil.com/steal\"}";
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo(state));
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_HttpsLocalhost_ReturnsNull()
    {
        // Only http is allowed, not https
        var json = "{\"return_to\":\"https://localhost:8400/callback\"}";
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo(state));
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_MalformedBase64_ReturnsNull()
    {
        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo("not-valid-base64!!!"));
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_MalformedJson_ReturnsNull()
    {
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{bad json"));

        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo(state));
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_MissingReturnToKey_ReturnsNull()
    {
        var json = "{\"other_key\":\"http://localhost:8400/callback\"}";
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo(state));
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_EmptyReturnTo_ReturnsNull()
    {
        var json = "{\"return_to\":\"\"}";
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo(state));
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_StripsTrailingSlash()
    {
        var json = "{\"return_to\":\"http://localhost:8400/auth/callback/\"}";
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        var result = AuthEndpoints.TryExtractLoopbackReturnTo(state);

        Assert.Equal("http://localhost:8400/auth/callback", result);
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_IpAddress127001_ReturnsNull()
    {
        // Only "localhost" is accepted, not 127.0.0.1
        var json = "{\"return_to\":\"http://127.0.0.1:8400/callback\"}";
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo(state));
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_InvalidUri_ReturnsNull()
    {
        // A return_to value that is not a valid absolute URI
        var json = "{\"return_to\":\"not-a-valid-uri\"}";
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo(state));
    }

    [Fact]
    public void TryExtractLoopbackReturnTo_RelativeUri_ReturnsNull()
    {
        var json = "{\"return_to\":\"/relative/path\"}";
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Null(AuthEndpoints.TryExtractLoopbackReturnTo(state));
    }
}
