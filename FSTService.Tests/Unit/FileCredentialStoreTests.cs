using System.Text.Json;
using FSTService.Auth;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class FileCredentialStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger<FileCredentialStore> _log = Substitute.For<ILogger<FileCredentialStore>>();

    public FileCredentialStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fst_cred_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string GetPath(string name = "creds.json") => Path.Combine(_tempDir, name);

    [Fact]
    public async Task LoadAsync_FileNotFound_ReturnsNull()
    {
        var store = new FileCredentialStore(GetPath("nonexistent.json"), _log);
        var result = await store.LoadAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips()
    {
        var path = GetPath();
        var store = new FileCredentialStore(path, _log);

        var creds = new StoredCredentials
        {
            AccountId = "acct_123",
            RefreshToken = "rt_456",
            DisplayName = "TestPlayer",
            SavedAt = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(creds);
        Assert.True(File.Exists(path));

        var loaded = await store.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal("acct_123", loaded!.AccountId);
        Assert.Equal("rt_456", loaded.RefreshToken);
        Assert.Equal("TestPlayer", loaded.DisplayName);
    }

    [Fact]
    public async Task SaveAsync_CreatesDirectoryIfNeeded()
    {
        var nestedPath = Path.Combine(_tempDir, "sub", "nested", "creds.json");
        var store = new FileCredentialStore(nestedPath, _log);

        var creds = new StoredCredentials
        {
            AccountId = "acct",
            RefreshToken = "rt",
        };

        await store.SaveAsync(creds);
        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_ReturnsNull()
    {
        var path = GetPath();
        await File.WriteAllTextAsync(path, "not valid json {{{");

        var store = new FileCredentialStore(path, _log);
        var result = await store.LoadAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadAsync_EmptyAccountId_ReturnsNull()
    {
        var path = GetPath();
        var json = JsonSerializer.Serialize(new StoredCredentials
        {
            AccountId = "",
            RefreshToken = "rt",
        });
        await File.WriteAllTextAsync(path, json);

        var store = new FileCredentialStore(path, _log);
        var result = await store.LoadAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadAsync_EmptyRefreshToken_ReturnsNull()
    {
        var path = GetPath();
        var json = JsonSerializer.Serialize(new StoredCredentials
        {
            AccountId = "acct",
            RefreshToken = "",
        });
        await File.WriteAllTextAsync(path, json);

        var store = new FileCredentialStore(path, _log);
        var result = await store.LoadAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsync_WritesIndentedJson()
    {
        var path = GetPath();
        var store = new FileCredentialStore(path, _log);

        await store.SaveAsync(new StoredCredentials
        {
            AccountId = "acct",
            RefreshToken = "rt",
        });

        var text = await File.ReadAllTextAsync(path);
        // Indented JSON has newlines
        Assert.Contains("\n", text);
    }

    [Fact]
    public void Implements_ICredentialStore()
    {
        var store = new FileCredentialStore(GetPath(), _log);
        Assert.IsAssignableFrom<ICredentialStore>(store);
    }
}
