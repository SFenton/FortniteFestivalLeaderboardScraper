using System.Text.Json;

namespace FSTService.Auth;

/// <summary>
/// Stores auth credentials (refresh token) as a JSON file on disk.
/// Suitable for single-account dev/test scenarios.
/// </summary>
public sealed class FileCredentialStore : ICredentialStore
{
    private readonly string _path;
    private readonly ILogger<FileCredentialStore> _log;

    public FileCredentialStore(string path, ILogger<FileCredentialStore> log)
    {
        _path = path;
        _log = log;
    }

    public async Task<StoredCredentials?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_path, ct);
            var creds = JsonSerializer.Deserialize<StoredCredentials>(json);
            if (creds is null || string.IsNullOrEmpty(creds.AccountId) || string.IsNullOrEmpty(creds.RefreshToken))
                return null;

            return creds;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load credentials from {Path}", _path);
            return null;
        }
    }

    public async Task SaveAsync(StoredCredentials credentials, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(credentials, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json, ct);
        _log.LogDebug("Credentials saved to {Path}", _path);
    }
}
