namespace FSTService.Auth;

/// <summary>
/// Abstracts storage of persisted auth credentials so we can swap
/// between file-based (dev) and DB-based (production) storage.
/// </summary>
public interface ICredentialStore
{
    Task<StoredCredentials?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(StoredCredentials credentials, CancellationToken ct = default);
    Task DeleteAsync(CancellationToken ct = default);
}
