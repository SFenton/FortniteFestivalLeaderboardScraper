using System.Security.Cryptography;
using System.Text;

namespace FSTService.Persistence;

internal static class BandIdentity
{
    public static string CreateBandId(string bandType, string teamKey)
    {
        var seed = Encoding.UTF8.GetBytes($"{bandType}:{teamKey}");
        var hash = SHA256.HashData(seed);
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);

        // Mark the deterministic id as a UUIDv5-style identifier.
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes).ToString("D");
    }
}