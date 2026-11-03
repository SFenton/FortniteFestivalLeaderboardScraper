using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>PIA's public server list, fetched directly rather than through an exit.</summary>
public interface IPiaServerListSource
{
    Task<IReadOnlyList<PiaServerAddress>> FetchAsync(CancellationToken ct);
}

/// <summary>
/// Reads PIA's server list API. Each response names only a few random servers
/// per region, so repeated fetches discover far more addresses than Gluetun's
/// image list, which every recreated exit shares.
/// </summary>
public sealed class PiaServerListClient : IPiaServerListSource
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _url;

    public PiaServerListClient(IHttpClientFactory httpFactory, IOptions<ScraperOptions> options)
    {
        _httpFactory = httpFactory;
        _url = options.Value.ProxyRegionRotationPiaServerListUrl;
    }

    public async Task<IReadOnlyList<PiaServerAddress>> FetchAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(nameof(PiaServerListClient));
        using var response = await http.GetAsync(
            new Uri(_url, UriKind.Absolute), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidDataException("The PIA server list exceeds 4 MiB.");
            buffer.Write(chunk, 0, read);
        }
        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>
    /// Reads the OpenVPN UDP IPv4 addresses of online regions from PIA's v7
    /// list, which is one JSON object followed by a detached signature.
    /// </summary>
    internal static IReadOnlyList<PiaServerAddress> Parse(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("regions", out var regions)
            || regions.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<PiaServerAddress>();
        foreach (var region in regions.EnumerateArray())
        {
            if (region.ValueKind != JsonValueKind.Object
                || (region.TryGetProperty("offline", out var offline)
                    && offline.ValueKind == JsonValueKind.True)
                || !region.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameElement.GetString())
                || !region.TryGetProperty("servers", out var servers)
                || servers.ValueKind != JsonValueKind.Object
                || !servers.TryGetProperty("ovpnudp", out var udp)
                || udp.ValueKind != JsonValueKind.Array)
                continue;
            var name = nameElement.GetString()!;
            foreach (var server in udp.EnumerateArray())
            {
                if (server.ValueKind == JsonValueKind.Object
                    && server.TryGetProperty("ip", out var ip)
                    && ip.ValueKind == JsonValueKind.String
                    && IPAddress.TryParse(ip.GetString(), out var address)
                    && address.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(new PiaServerAddress(address, name));
            }
        }
        return result;
    }
}
