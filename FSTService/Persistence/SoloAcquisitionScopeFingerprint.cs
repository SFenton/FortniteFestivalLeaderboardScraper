using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FSTService.Persistence;

internal readonly record struct SoloAcquisitionScopeContract(
    int Count,
    int FingerprintVersion,
    string Fingerprint);

internal static class SoloAcquisitionScopeFingerprint
{
    internal const int Version = 1;
    private static readonly byte[] Domain =
        "fst-solo-acquisition-scope\0v1\0"u8.ToArray();

    internal static SoloAcquisitionScopeContract Create(
        IEnumerable<(string SongId, string Instrument)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var canonicalPairs = pairs
            .Select(static pair => (
                SongId: ValidateValue(pair.SongId, nameof(pair.SongId)),
                Instrument: ValidateValue(
                    pair.Instrument,
                    nameof(pair.Instrument))))
            .Distinct()
            .OrderBy(static pair => pair.Instrument, StringComparer.Ordinal)
            .ThenBy(static pair => pair.SongId, StringComparer.Ordinal)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Domain);
        foreach (var pair in canonicalPairs)
        {
            AppendLengthPrefixedUtf8(hash, pair.Instrument);
            AppendLengthPrefixedUtf8(hash, pair.SongId);
        }

        return new SoloAcquisitionScopeContract(
            canonicalPairs.Length,
            Version,
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static string ValidateValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "Solo acquisition scope keys must be non-empty.",
                parameterName);
        return value;
    }

    private static void AppendLengthPrefixedUtf8(
        IncrementalHash hash,
        string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
