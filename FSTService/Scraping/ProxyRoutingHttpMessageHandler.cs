namespace FSTService.Scraping;

internal sealed class ProxyRoutingHttpMessageHandler : HttpMessageHandler
{
    private readonly ProxyPool _pool;

    public ProxyRoutingHttpMessageHandler(ProxyPool pool)
    {
        _pool = pool;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var lease = await _pool.AcquireAsync(cancellationToken);
        if (lease is null)
            throw new InvalidOperationException("Proxy routing handler was used without configured proxy endpoints.");

        request.Options.Set(ProxyRequestState.EndpointIndex, lease.Index);
        request.Options.Set(ProxyRequestState.EndpointName, lease.Name);
        request.Options.Set(ProxyRequestState.EndpointProxyUri, lease.ProxyUri);

        try
        {
            return await lease.Invoker.SendAsync(request, cancellationToken);
        }
        finally
        {
            lease.Dispose();
        }
    }
}
