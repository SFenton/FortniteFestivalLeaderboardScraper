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

    public interface IVersionedSongCatalogPersistence
    {
        Task<SongCatalogPersistenceToken> SaveSongsVersionedAsync(
            IEnumerable<Song> songs);
    }
}
