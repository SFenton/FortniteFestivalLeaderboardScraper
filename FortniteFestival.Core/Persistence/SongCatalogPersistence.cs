using System.Collections.Generic;
using System.Threading.Tasks;

namespace FortniteFestival.Core.Persistence
{
    public sealed class SongCatalogPersistenceToken
    {
        public long CatalogVersion { get; }
        public int SchemaVersion { get; }
        public string ContentHash { get; }
        public int SongCount { get; }

        public SongCatalogPersistenceToken(
            long catalogVersion,
            int schemaVersion,
            string contentHash,
            int songCount)
        {
            CatalogVersion = catalogVersion;
            SchemaVersion = schemaVersion;
            ContentHash = contentHash;
            SongCount = songCount;
        }
    }

    public sealed class SongCatalogSyncResult
    {
        public bool ProviderRequestSucceeded { get; }
        public bool IsExact { get; }
        public bool SafetyMergeApplied { get; }
        public int ProviderSongCount { get; }
        public int CatalogSongCount { get; }
        public int DroppedProviderObjectCount { get; }
        public string FailureReason { get; }
        public SongCatalogPersistenceToken PersistenceToken { get; }

        public SongCatalogSyncResult(
            bool providerRequestSucceeded,
            bool isExact,
            bool safetyMergeApplied,
            int providerSongCount,
            int catalogSongCount,
            int droppedProviderObjectCount,
            string failureReason,
            SongCatalogPersistenceToken persistenceToken)
        {
            ProviderRequestSucceeded = providerRequestSucceeded;
            IsExact = isExact;
            SafetyMergeApplied = safetyMergeApplied;
            ProviderSongCount = providerSongCount;
            CatalogSongCount = catalogSongCount;
            DroppedProviderObjectCount = droppedProviderObjectCount;
            FailureReason = failureReason;
            PersistenceToken = persistenceToken;
        }
    }

    public interface IVersionedSongCatalogPersistence
    {
        Task<SongCatalogPersistenceToken> SaveSongsVersionedAsync(
            IEnumerable<Song> songs);
    }
}
