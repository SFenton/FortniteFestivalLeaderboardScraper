namespace FSTService.Scraping.Capture;

internal sealed class CaptureResponseLimitHandler
    : DelegatingHandler
{
    private const int BufferSize = 64 * 1024;
    private readonly long _maximumBytes;

    internal CaptureResponseLimitHandler(
        HttpMessageHandler innerHandler,
        long maximumBytes)
        : base(innerHandler)
    {
        if (maximumBytes <= 0 ||
            maximumBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes));
        }
        _maximumBytes = maximumBytes;
    }

    protected override async Task<HttpResponseMessage>
        SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(
            request,
            cancellationToken);
        if (response.Content is null)
            return response;
        var contentLength =
            response.Content.Headers.ContentLength;
        if (contentLength.HasValue &&
            contentLength.Value > _maximumBytes)
        {
            response.Dispose();
            throw new
                ResponseBodyLimitExceededException();
        }

        try
        {
            await using var source =
                await response.Content
                    .ReadAsStreamAsync(
                        cancellationToken);
            using var destination =
                new MemoryStream(
                    capacity: (int)Math.Min(
                        contentLength ?? 0L,
                        _maximumBytes));
            var buffer = new byte[BufferSize];
            while (true)
            {
                var read = await source.ReadAsync(
                    buffer,
                    cancellationToken);
                if (read == 0)
                    break;
                if (destination.Length >
                    _maximumBytes - read)
                {
                    throw new
                        ResponseBodyLimitExceededException();
                }
                destination.Write(
                    buffer,
                    0,
                    read);
            }

            var originalContent = response.Content;
            var replacement = new ByteArrayContent(
                destination.ToArray());
            foreach (var header in
                     originalContent.Headers)
            {
                replacement.Headers
                    .TryAddWithoutValidation(
                        header.Key,
                        header.Value);
            }
            response.Content = replacement;
            originalContent.Dispose();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
