import { useState, useMemo } from 'react';
import { Link } from 'react-router-dom';
import { useFestival } from '../contexts/FestivalContext';
import type { Song } from '../models';
import { Colors, Font, Gap, Radius, Layout, Size, MaxWidth } from '../theme';

type SortMode = 'title' | 'artist';

export default function SongsPage() {
  const {
    state: { songs, isLoading, error },
  } = useFestival();
  const [search, setSearch] = useState('');
  const [sort, setSort] = useState<SortMode>('title');

  const filtered = useMemo(() => {
    const q = search.toLowerCase();
    let list = songs;
    if (q) {
      list = list.filter(
        (s) =>
          s.title.toLowerCase().includes(q) ||
          s.artist.toLowerCase().includes(q),
      );
    }
    return list.slice().sort((a, b) => {
      if (sort === 'title') {
        return a.title.localeCompare(b.title);
      }
      return a.artist.localeCompare(b.artist);
    });
  }, [songs, search, sort]);

  if (isLoading) {
    return <div style={styles.center}>Loading songs…</div>;
  }

  if (error) {
    return <div style={styles.center}>{error}</div>;
  }

  return (
    <div style={styles.page}>
      <div style={styles.container}>
        <h1 style={styles.heading}>Songs</h1>
        <div style={styles.toolbar}>
          <input
            style={styles.searchInput}
            placeholder="Search songs or artists…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <div style={styles.sortGroup}>
            <SortButton
              label="Title"
              active={sort === 'title'}
              onClick={() => setSort('title')}
            />
            <SortButton
              label="Artist"
              active={sort === 'artist'}
              onClick={() => setSort('artist')}
            />
          </div>
        </div>
        <div style={styles.count}>
          {filtered.length} song{filtered.length !== 1 ? 's' : ''}
        </div>
        <div style={styles.list}>
          {filtered.map((song) => (
            <SongRow key={song.songId} song={song} />
          ))}
        </div>
      </div>
    </div>
  );
}

function SongRow({ song }: { song: Song }) {
  return (
    <Link to={`/songs/${song.songId}`} style={styles.row}>
      {song.albumArt ? (
        <img src={song.albumArt} alt="" style={styles.thumb} loading="lazy" />
      ) : (
        <div style={{ ...styles.thumb, ...styles.thumbPlaceholder }} />
      )}
      <div style={styles.rowText}>
        <span style={styles.rowTitle}>{song.title}</span>
        <span style={styles.rowArtist}>{song.artist}</span>
      </div>
      {song.tempo ? (
        <span style={styles.rowBpm}>{song.tempo} BPM</span>
      ) : null}
    </Link>
  );
}

function SortButton({
  label,
  active,
  onClick,
}: {
  label: string;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      onClick={onClick}
      style={{
        ...styles.sortButton,
        ...(active ? styles.sortButtonActive : {}),
      }}
    >
      {label}
    </button>
  );
}

const styles: Record<string, React.CSSProperties> = {
  page: {
    minHeight: '100vh',
    backgroundColor: Colors.backgroundApp,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
  },
  container: {
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px`,
  },
  heading: {
    fontSize: Font.title,
    fontWeight: 700,
    marginBottom: Gap.xl,
  },
  toolbar: {
    display: 'flex',
    gap: Gap.xl,
    alignItems: 'center',
    flexWrap: 'wrap' as const,
    marginBottom: Gap.md,
  },
  searchInput: {
    flex: 1,
    minWidth: 200,
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderRadius: Radius.sm,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.backgroundCard,
    color: Colors.textPrimary,
    fontSize: Font.md,
    outline: 'none',
  },
  sortGroup: {
    display: 'flex',
    gap: Gap.sm,
  },
  sortButton: {
    padding: `${Gap.sm}px ${Gap.xl}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.transparent,
    color: Colors.textTertiary,
    fontSize: Font.sm,
    cursor: 'pointer',
  },
  sortButtonActive: {
    backgroundColor: Colors.chipSelectedBg,
    color: Colors.accentBlue,
    borderColor: Colors.accentBlue,
  },
  count: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
    marginBottom: Gap.md,
  },
  list: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderRadius: Radius.md,
    backgroundColor: Colors.backgroundCard,
    border: `1px solid ${Colors.borderSubtle}`,
    textDecoration: 'none',
    color: 'inherit',
    transition: 'background-color 0.15s',
  },
  thumb: {
    width: Size.thumb,
    height: Size.thumb,
    borderRadius: Radius.xs,
    objectFit: 'cover' as const,
    flexShrink: 0,
  },
  thumbPlaceholder: {
    backgroundColor: Colors.purplePlaceholder,
  },
  rowText: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
    minWidth: 0,
    flex: 1,
  },
  rowTitle: {
    fontSize: Font.md,
    fontWeight: 600,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  rowArtist: {
    fontSize: Font.sm,
    color: Colors.textSubtle,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  rowBpm: {
    fontSize: Font.xs,
    color: Colors.textMuted,
    flexShrink: 0,
  },
  center: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '100vh',
    color: Colors.textSecondary,
    backgroundColor: Colors.backgroundApp,
    fontSize: Font.lg,
  },
};
