using System.Net;

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
        ProxyPool.ProxyLease? lease =
            await _pool.AcquireAsync(cancellationToken);
        if (lease is null)
            throw new InvalidOperationException("Proxy routing handler was used without configured proxy endpoints.");

        request.Options.Set(ProxyRequestState.EndpointIndex, lease.Index);
        request.Options.Set(ProxyRequestState.EndpointName, lease.Name);
        request.Options.Set(ProxyRequestState.EndpointProxyUri, lease.ProxyUri);
        _pool.PrepareRequest(request);
        if (request.Options.TryGetValue(
                ProxyRequestState.WireSendRecorder,
                out var recordWireSend))
        {
            recordWireSend();
        }

        try
        {
            var response = await lease.Invoker
                .SendAsync(
                    request,
                    cancellationToken);
            var content = response.Content;
            if (content is null)
                return response;

            response.Content =
                new ProxyLeaseHttpContent(
                    content,
                    lease);
            lease = null;
            return response;
        }
        finally
        {
            lease?.Dispose();
        }
    }
}

internal sealed class ProxyLeaseHttpContent
    : HttpContent
{
    private readonly HttpContent _inner;
    private IDisposable? _lease;

    internal ProxyLeaseHttpContent(
        HttpContent inner,
        IDisposable lease)
    {
        _inner = inner;
        _lease = lease;
        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(
                header.Key,
                header.Value);
        }
    }

    protected override bool TryComputeLength(
        out long length)
    {
        if (_inner.Headers.ContentLength is
            { } contentLength)
        {
            length = contentLength;
            return true;
        }
        length = 0;
        return false;
    }

    protected override async Task
        SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
    {
        try
        {
            await _inner.CopyToAsync(stream);
        }
        finally
        {
            ReleaseLease();
        }
    }

    protected override async Task
        SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
    {
        try
        {
            await _inner.CopyToAsync(
                stream,
                cancellationToken);
        }
        finally
        {
            ReleaseLease();
        }
    }

    protected override async Task<Stream>
        CreateContentReadStreamAsync()
    {
        try
        {
            return new ProxyLeaseStream(
                await _inner
                    .ReadAsStreamAsync(),
                ReleaseLease);
        }
        catch
        {
            ReleaseLease();
            throw;
        }
    }

    protected override async Task<Stream>
        CreateContentReadStreamAsync(
            CancellationToken cancellationToken)
    {
        try
        {
            return new ProxyLeaseStream(
                await _inner.ReadAsStreamAsync(
                    cancellationToken),
                ReleaseLease);
        }
        catch
        {
            ReleaseLease();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _inner.Dispose();
            }
            finally
            {
                ReleaseLease();
            }
        }
        base.Dispose(disposing);
    }

    private void ReleaseLease() =>
        Interlocked.Exchange(
            ref _lease,
            null)?.Dispose();
}

internal sealed class ProxyLeaseStream
    : Stream
{
    private readonly Stream _inner;
    private Action? _release;

    internal ProxyLeaseStream(
        Stream inner,
        Action release)
    {
        _inner = inner;
        _release = release;
    }

    public override bool CanRead =>
        _inner.CanRead;
    public override bool CanSeek =>
        _inner.CanSeek;
    public override bool CanWrite =>
        _inner.CanWrite;
    public override long Length =>
        _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() =>
        _inner.Flush();

    public override Task FlushAsync(
        CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        try
        {
            var read = _inner.Read(
                buffer,
                offset,
                count);
            if (read == 0)
                Release();
            return read;
        }
        catch
        {
            Release();
            throw;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        try
        {
            var read = _inner.Read(buffer);
            if (read == 0)
                Release();
            return read;
        }
        catch
        {
            Release();
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken =
            default)
    {
        try
        {
            var read = await _inner.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0)
                Release();
            return read;
        }
        catch
        {
            Release();
            throw;
        }
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return ReadArrayAsync();

        async Task<int> ReadArrayAsync()
        {
            try
            {
                var read = await _inner.ReadAsync(
                    buffer,
                    offset,
                    count,
                    cancellationToken);
                if (read == 0)
                    Release();
                return read;
            }
            catch
            {
                Release();
                throw;
            }
        }
    }

    public override long Seek(
        long offset,
        SeekOrigin origin) =>
        _inner.Seek(offset, origin);

    public override void SetLength(long value) =>
        _inner.SetLength(value);

    public override void Write(
        byte[] buffer,
        int offset,
        int count) =>
        _inner.Write(buffer, offset, count);

    public override void Write(
        ReadOnlySpan<byte> buffer) =>
        _inner.Write(buffer);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken =
            default) =>
        _inner.WriteAsync(
            buffer,
            cancellationToken);

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        _inner.WriteAsync(
            buffer,
            offset,
            count,
            cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _inner.Dispose();
            }
            finally
            {
                Release();
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await _inner.DisposeAsync();
        }
        finally
        {
            Release();
        }
        GC.SuppressFinalize(this);
    }

    private void Release() =>
        Interlocked.Exchange(
            ref _release,
            null)?.Invoke();
}
