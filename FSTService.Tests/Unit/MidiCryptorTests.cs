using System.Security.Cryptography;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class MidiCryptorTests
{
    [Fact]
    public void ParseHexKey_valid_32_char_hex_returns_16_bytes()
    {
        var key = MidiCryptor.ParseHexKey("0123456789abcdef0123456789abcdef");
        Assert.Equal(16, key.Length);
        Assert.Equal(0x01, key[0]);
        Assert.Equal(0xef, key[15]);
    }

    [Fact]
    public void ParseHexKey_valid_64_char_hex_returns_32_bytes()
    {
        var key = MidiCryptor.ParseHexKey("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void ParseHexKey_trims_whitespace()
    {
        var key = MidiCryptor.ParseHexKey("  0123456789abcdef0123456789abcdef  ");
        Assert.Equal(16, key.Length);
    }

    [Fact]
    public void ParseHexKey_wrong_length_throws()
    {
        Assert.Throws<ArgumentException>(() => MidiCryptor.ParseHexKey("0123456789abcdef"));
    }

    [Fact]
    public void ParseHexKey_empty_throws()
    {
        Assert.Throws<ArgumentException>(() => MidiCryptor.ParseHexKey(""));
        Assert.Throws<ArgumentException>(() => MidiCryptor.ParseHexKey("   "));
    }

    [Fact]
    public void Decrypt_wrong_key_length_throws()
    {
        Assert.Throws<ArgumentException>(() => MidiCryptor.Decrypt(new byte[32], new byte[8]));
    }

    [Fact]
    public void Decrypt_null_key_throws()
    {
        Assert.Throws<ArgumentNullException>(() => MidiCryptor.Decrypt(new byte[32], null!));
    }

    [Fact]
    public void Decrypt_roundtrip_with_aes_ecb()
    {
        // Encrypt known plaintext with AES-ECB, then verify Decrypt recovers it
        var key = new byte[16];
        RandomNumberGenerator.Fill(key);

        var plaintext = new byte[48]; // 3 blocks
        RandomNumberGenerator.Fill(plaintext);

        // Encrypt
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        // Decrypt using our implementation
        var decrypted = MidiCryptor.Decrypt(ciphertext, key);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_handles_partial_final_block()
    {
        // Input that's not a multiple of 16 — should be padded with zeros
        var key = new byte[16];
        RandomNumberGenerator.Fill(key);

        // 20 bytes = 1 full block + 4 bytes
        var input = new byte[20];
        RandomNumberGenerator.Fill(input);

        // Should not throw — the partial block gets zero-padded
        var result = MidiCryptor.Decrypt(input, key);

        // Result is 2 full blocks (32 bytes) since input gets padded to 32
        Assert.Equal(32, result.Length);
    }

    [Fact]
    public void ComputeHash_returns_lowercase_hex()
    {
        var data = "hello world"u8;
        var hash = MidiCryptor.ComputeHash(data);

        Assert.Equal(64, hash.Length); // SHA256 = 32 bytes = 64 hex chars
        Assert.Equal(hash, hash.ToLowerInvariant()); // must be lowercase
    }

    [Fact]
    public void ComputeHash_deterministic()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var hash1 = MidiCryptor.ComputeHash(data);
        var hash2 = MidiCryptor.ComputeHash(data);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_different_input_different_hash()
    {
        var hash1 = MidiCryptor.ComputeHash(new byte[] { 1, 2, 3 });
        var hash2 = MidiCryptor.ComputeHash(new byte[] { 4, 5, 6 });
        Assert.NotEqual(hash1, hash2);
    }
}
