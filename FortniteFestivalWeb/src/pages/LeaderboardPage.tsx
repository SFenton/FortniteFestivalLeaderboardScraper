import { useEffect, useState, useCallback } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useFestival } from '../contexts/FestivalContext';
import { api } from '../api/client';
import {
  INSTRUMENT_LABELS,
  type InstrumentKey,
  type LeaderboardEntry,
} from '../models';
import { Colors, Font, Gap, Radius, Layout, MaxWidth } from '../theme';

const PAGE_SIZE = 200;

export default function LeaderboardPage() {
  const { songId, instrument } = useParams<{
    songId: string;
    instrument: string;
  }>();
  const {
    state: { songs },
  } = useFestival();

  const song = songs.find((s) => s.songId === songId);
  const instKey = instrument as InstrumentKey;
  const instLabel = INSTRUMENT_LABELS[instKey] ?? instrument;

  const [entries, setEntries] = useState<LeaderboardEntry[]>([]);
  const [totalEntries, setTotalEntries] = useState(0);
  const [localEntries, setLocalEntries] = useState(0);
  const [page, setPage] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const totalPages = Math.max(1, Math.ceil(localEntries / PAGE_SIZE));

  const fetchPage = useCallback(
    async (pageNum: number) => {
      if (!songId || !instrument) return;
      setLoading(true);
      setError(null);
      try {
        const res = await api.getLeaderboard(
          songId,
          instKey,
          PAGE_SIZE,
          pageNum * PAGE_SIZE,
        );
        setEntries(res.entries);
        setTotalEntries(res.totalEntries);
        setLocalEntries(res.localEntries ?? res.totalEntries);
        setPage(pageNum);
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Failed to load leaderboard');
      } finally {
        setLoading(false);
      }
    },
    [songId, instrument, instKey],
  );

  useEffect(() => {
    void fetchPage(0);
  }, [fetchPage]);

  if (!songId || !instrument) {
    return <div style={styles.center}>Not found</div>;
  }

  const startRank = page * PAGE_SIZE;

  return (
    <div style={styles.page}>
      {song?.albumArt && (
        <div
          style={{
            ...styles.bgImage,
            backgroundImage: `url(${song.albumArt})`,
          }}
        />
      )}
      <div style={styles.bgDim} />
      <div style={styles.container}>
        <Link to={`/songs/${songId}`} style={styles.backLink}>
          ← Back to {song?.title ?? 'Song'}
        </Link>

        <div style={styles.header}>
          {song?.albumArt ? (
            <img src={song.albumArt} alt="" style={styles.headerArt} />
          ) : (
            <div style={{ ...styles.headerArt, ...styles.artPlaceholder }} />
          )}
          <div>
            <h1 style={styles.songTitle}>{song?.title ?? songId}</h1>
            <p style={styles.songArtist}>{song?.artist ?? 'Unknown Artist'}</p>
            <span style={styles.instBadge}>{instLabel}</span>
          </div>
        </div>

        <div style={styles.meta}>
          {totalEntries.toLocaleString()} total entries
          {totalPages > 1 && (
            <span style={styles.metaPage}>
              {' '}· Page {page + 1} of {totalPages}
            </span>
          )}
        </div>

        {loading && <div style={styles.center}>Loading…</div>}
        {error && <div style={styles.centerError}>{error}</div>}

        {!loading && !error && (
          <>
            <div style={styles.table}>
              <div style={styles.tableHeader}>
                <span style={styles.colRank}>Rank</span>
                <span style={styles.colName}>Player</span>
                <span style={styles.colScore}>Score</span>
                <span style={styles.colAcc}>Accuracy</span>
                <span style={styles.colFC}>FC</span>
                <span style={styles.colStars}>Stars</span>
              </div>
              {entries.map((e, i) => (
                <div key={e.accountId} style={styles.tableRow}>
                  <span style={styles.colRank}>#{startRank + i + 1}</span>
                  <Link
                    to={`/player/${e.accountId}`}
                    style={{ ...styles.colName, textDecoration: 'none', color: 'inherit' }}
                  >
                    {e.displayName ?? e.accountId.slice(0, 12)}
                  </Link>
                  <span style={styles.colScore}>
                    {e.score.toLocaleString()}
                  </span>
                  <span style={styles.colAcc}>
                    {e.accuracy != null
                      ? `${(e.accuracy / 10000).toFixed(2)}%`
                      : '—'}
                  </span>
                  <span style={styles.colFC}>
                    {e.isFullCombo ? (
                      <span style={styles.fcBadge}>FC</span>
                    ) : (
                      '—'
                    )}
                  </span>
                  <span style={styles.colStars}>
                    {e.stars != null && e.stars > 0
                      ? '★'.repeat(Math.min(e.stars, 6))
                      : '—'}
                  </span>
                </div>
              ))}
              {entries.length === 0 && (
                <div style={styles.emptyRow}>No entries on this page</div>
              )}
            </div>

            {totalPages > 1 && (
              <div style={styles.pagination}>
                <button
                  style={{
                    ...styles.pageButton,
                    ...(page === 0 ? styles.pageButtonDisabled : {}),
                  }}
                  disabled={page === 0}
                  onClick={() => void fetchPage(0)}
                >
                  « First
                </button>
                <button
                  style={{
                    ...styles.pageButton,
                    ...(page === 0 ? styles.pageButtonDisabled : {}),
                  }}
                  disabled={page === 0}
                  onClick={() => void fetchPage(page - 1)}
                >
                  ‹ Prev
                </button>
                <span style={styles.pageInfo}>
                  Page {page + 1} of {totalPages}
                </span>
                <button
                  style={{
                    ...styles.pageButton,
                    ...(page >= totalPages - 1
                      ? styles.pageButtonDisabled
                      : {}),
                  }}
                  disabled={page >= totalPages - 1}
                  onClick={() => void fetchPage(page + 1)}
                >
                  Next ›
                </button>
                <button
                  style={{
                    ...styles.pageButton,
                    ...(page >= totalPages - 1
                      ? styles.pageButtonDisabled
                      : {}),
                  }}
                  disabled={page >= totalPages - 1}
                  onClick={() => void fetchPage(totalPages - 1)}
                >
                  Last »
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  page: {
    minHeight: '100vh',
    backgroundColor: Colors.backgroundApp,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
    position: 'relative' as const,
    overflow: 'hidden' as const,
  },
  bgImage: {
    position: 'fixed' as const,
    inset: 0,
    backgroundSize: 'cover',
    backgroundPosition: 'center',
    opacity: 0.9,
    pointerEvents: 'none' as const,
  },
  bgDim: {
    position: 'fixed' as const,
    inset: 0,
    backgroundColor: Colors.overlayDark,
    pointerEvents: 'none' as const,
  },
  container: {
    position: 'relative' as const,
    zIndex: 1,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px`,
  },
  backLink: {
    color: Colors.accentBlue,
    textDecoration: 'none',
    fontSize: Font.md,
    marginBottom: Gap.xl,
    display: 'inline-block',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.section,
    marginTop: Gap.xl,
    marginBottom: Gap.xl,
  },
  headerArt: {
    width: 80,
    height: 80,
    borderRadius: Radius.md,
    objectFit: 'cover' as const,
    flexShrink: 0,
  },
  artPlaceholder: {
    backgroundColor: Colors.purplePlaceholder,
  },
  songTitle: {
    fontSize: Font.title,
    fontWeight: 700,
    marginBottom: Gap.xs,
  },
  songArtist: {
    fontSize: Font.md,
    color: Colors.textSubtle,
    marginBottom: Gap.sm,
  },
  instBadge: {
    fontSize: Font.sm,
    fontWeight: 600,
    color: Colors.accentPurple,
    backgroundColor: Colors.accentPurpleDark,
    padding: `${Gap.xs}px ${Gap.md}px`,
    borderRadius: Radius.xs,
  },
  meta: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
    marginBottom: Gap.xl,
  },
  metaPage: {
    color: Colors.textMuted,
  },
  table: {
    display: 'flex',
    flexDirection: 'column' as const,
    border: `1px solid ${Colors.glassBorder}`,
    borderRadius: Radius.lg,
    overflow: 'hidden',
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
  },
  tableHeader: {
    display: 'flex',
    alignItems: 'center',
    padding: `${Gap.md}px ${Gap.xl}px`,
    backgroundColor: Colors.accentPurpleDark,
    fontWeight: 600,
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  tableRow: {
    display: 'flex',
    alignItems: 'center',
    padding: `${Gap.sm + 2}px ${Gap.xl}px`,
    borderBottom: `1px solid ${Colors.glassBorder}`,
    fontSize: Font.md,
  },
  emptyRow: {
    padding: `${Gap.xl}px`,
    textAlign: 'center' as const,
    color: Colors.textMuted,
  },
  colRank: {
    width: 64,
    flexShrink: 0,
    color: Colors.textTertiary,
    fontSize: Font.sm,
  },
  colName: {
    flex: 1,
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
  },
  colScore: {
    width: 100,
    flexShrink: 0,
    textAlign: 'right' as const,
    fontWeight: 600,
    color: Colors.accentBlueBright,
    fontVariantNumeric: 'tabular-nums',
  },
  colAcc: {
    width: 80,
    flexShrink: 0,
    textAlign: 'right' as const,
    color: Colors.textSecondary,
    fontSize: Font.sm,
    fontVariantNumeric: 'tabular-nums',
  },
  colFC: {
    width: 48,
    flexShrink: 0,
    textAlign: 'center' as const,
  },
  colStars: {
    width: 80,
    flexShrink: 0,
    textAlign: 'center' as const,
    color: Colors.gold,
    fontSize: Font.xs,
  },
  fcBadge: {
    fontSize: Font.xs,
    fontWeight: 700,
    color: Colors.gold,
    backgroundColor: Colors.goldBg,
    padding: `${Gap.xs}px ${Gap.sm}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.goldStroke}`,
  },
  pagination: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.md,
    marginTop: Gap.section,
    marginBottom: Gap.section,
  },
  pageButton: {
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderRadius: Radius.sm,
    border: `1px solid ${Colors.glassBorder}`,
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    color: Colors.textPrimary,
    fontSize: Font.sm,
    cursor: 'pointer',
    transition: 'background-color 0.15s',
  },
  pageButtonDisabled: {
    opacity: 0.4,
    cursor: 'default',
  },
  pageInfo: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
    padding: `0 ${Gap.md}px`,
  },
  center: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: `${Gap.section * 2}px 0`,
    color: Colors.textSecondary,
    fontSize: Font.lg,
  },
  centerError: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: `${Gap.section * 2}px 0`,
    color: Colors.statusRed,
    fontSize: Font.lg,
  },
};
