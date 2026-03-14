using System.Security.Cryptography;

namespace FSTService.Scraping;

/// <summary>
/// Decrypts/encrypts Fortnite Festival MIDI .dat files using AES-128-ECB.
/// Port of FNFpaths fnf.py.
/// </summary>
public static class MidiCryptor
{
    /// <summary>
    /// Decrypt an encrypted .dat file to a .mid file (in memory).
    /// The final block is padded with zeros if shorter than 16 bytes.
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> encryptedData, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 16 && key.Length != 32)
            throw new ArgumentException("Key must be 16 bytes (AES-128) or 32 bytes (AES-256).", nameof(key));

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None; // Manual padding like FNFpaths

        using var decryptor = aes.CreateDecryptor();

        // Process in 16-byte blocks, same as FNFpaths
        int blockCount = (encryptedData.Length + 15) / 16;
        var output = new byte[blockCount * 16];
        var inputBlock = new byte[16];

        for (int i = 0; i < blockCount; i++)
        {
            int offset = i * 16;
            int remaining = encryptedData.Length - offset;
            int toCopy = Math.Min(16, remaining);

            // Clear block and copy data (pad final short block with zeros)
            Array.Clear(inputBlock);
            encryptedData.Slice(offset, toCopy).CopyTo(inputBlock);

            decryptor.TransformBlock(inputBlock, 0, 16, output, offset);
        }

        return output;
    }

    /// <summary>
    /// Parse a hex string into a 16-byte AES key.
    /// </summary>
    public static byte[] ParseHexKey(string hexKey)
    {
        if (string.IsNullOrWhiteSpace(hexKey))
            throw new ArgumentException("MIDI encryption key is required.", nameof(hexKey));

        var bytes = Convert.FromHexString(hexKey.Trim());
        if (bytes.Length != 16 && bytes.Length != 32)
            throw new ArgumentException($"Key must be 32 hex chars (AES-128) or 64 hex chars (AES-256), got {bytes.Length} bytes.", nameof(hexKey));

        return bytes;
    }

    /// <summary>
    /// Computes SHA256 hash of data, returned as a lowercase hex string.
    /// Used for .dat file change detection.
    /// </summary>
    public static string ComputeHash(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexStringLower(hash);
    }
}
