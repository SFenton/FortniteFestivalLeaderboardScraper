import { useState, useMemo, useEffect, useCallback } from 'react';
import { Link } from 'react-router-dom';
import { useFestival } from '../contexts/FestivalContext';
import { useSyncStatus } from '../hooks/useSyncStatus';
import { api } from '../api/client';
import type { Song, PlayerScore, PlayerResponse, InstrumentKey } from '../models';
import { INSTRUMENT_KEYS, INSTRUMENT_LABELS } from '../models';
import { Colors, Font, Gap, Radius, Layout, Size, MaxWidth } from '../theme';

type SortMode = 'title' | 'artist';

type Props = {
  accountId?: string;
};

export default function SongsPage({ accountId }: Props) {
  const {
    state: { songs, isLoading, error },
  } = useFestival();
  const [search, setSearch] = useState('');
  const [sort, setSort] = useState<SortMode>('title');
  const [instrument, setInstrument] = useState<InstrumentKey>('Solo_Guitar');
  const [playerData, setPlayerData] = useState<PlayerResponse | null>(null);
  const [playerLoading, setPlayerLoading] = useState(false);
  const { isSyncing, phase, backfillProgress, historyProgress, justCompleted, clearCompleted } =
    useSyncStatus(accountId);

  const fetchPlayer = useCallback(async (accountId: string) => {
    setPlayerLoading(true);
    try {
      const res = await api.getPlayer(accountId);
      setPlayerData(res);
    } catch {
      setPlayerData(null);
    } finally {
      setPlayerLoading(false);
    }
  }, []);

  useEffect(() => {
    if (accountId) {
      void fetchPlayer(accountId);
    } else {
      setPlayerData(null);
    }
  }, [accountId, fetchPlayer]);

  // Auto-reload player data when sync completes
  useEffect(() => {
    if (justCompleted && accountId) {
      clearCompleted();
      void fetchPlayer(accountId);
    }
  }, [justCompleted, clearCompleted, accountId, fetchPlayer]);

  // Build lookup: songId → PlayerScore for the selected instrument
  const scoreMap = useMemo(() => {
    if (!playerData) return new Map<string, PlayerScore>();
    const map = new Map<string, PlayerScore>();
    for (const s of playerData.scores) {
      if (s.instrument === instrument) {
        map.set(s.songId, s);
      }
    }
    return map;
  }, [playerData, instrument]);

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

  const hasPlayer = !!playerData;

  if (isLoading) {
    return <div style={styles.center}>Loading songs…</div>;
  }

  if (error) {
    return <div style={styles.center}>{error}</div>;
  }

  return (
    <div style={styles.page}>
      <div style={styles.stickyHeader}>
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
        </div>
      </div>
      <div style={styles.container}>
        {hasPlayer && (
          <div style={styles.instrumentBar}>
            {INSTRUMENT_KEYS.map((key) => (
              <button
                key={key}
                onClick={() => setInstrument(key)}
                style={{
                  ...styles.instrumentChip,
                  ...(instrument === key ? styles.instrumentChipActive : {}),
                }}
              >
                {INSTRUMENT_LABELS[key]}
              </button>
            ))}
            {playerLoading && <span style={styles.loadingDot}>loading…</span>}
          </div>
        )}
        {isSyncing && (
          <div style={styles.syncBanner}>
            <div style={styles.syncSpinner} />
            <div style={{ flex: 1 }}>
              <div style={styles.syncTitle}>
                {phase === 'backfill' ? 'Syncing Data' : 'Building Score History'}
              </div>
              <div style={styles.syncSubtitle}>
                {phase === 'backfill'
                  ? 'Fetching scores from leaderboards…'
                  : 'Reconstructing score history across seasons…'}
              </div>
              {phase === 'backfill' && backfillProgress > 0 && (
                <div style={{ marginTop: Gap.md }}>
                  <div style={styles.syncProgressLabel}>
                    <span>Syncing scores</span>
                    <span>{(backfillProgress * 100).toFixed(1)}%</span>
                  </div>
                  <div style={styles.syncProgressOuter}>
                    <div
                      style={{
                        ...styles.syncProgressInner,
                        width: `${Math.round(backfillProgress * 100)}%`,
                      }}
                    />
                  </div>
                </div>
              )}
              {phase === 'history' && (
                <>
                  <div style={{ marginTop: Gap.md }}>
                    <div style={styles.syncProgressLabel}>
                      <span>Syncing scores</span>
                      <span>100.0%</span>
                    </div>
                    <div style={styles.syncProgressOuter}>
                      <div style={{ ...styles.syncProgressInner, width: '100%' }} />
                    </div>
                  </div>
                  {historyProgress > 0 && (
                    <div style={{ marginTop: Gap.sm }}>
                      <div style={styles.syncProgressLabel}>
                        <span>Building history</span>
                        <span>{(historyProgress * 100).toFixed(1)}%</span>
                      </div>
                      <div style={styles.syncProgressOuter}>
                        <div
                          style={{
                            ...styles.syncProgressInner,
                            width: `${Math.round(historyProgress * 100)}%`,
                          }}
                        />
                      </div>
                    </div>
                  )}
                </>
              )}
            </div>
          </div>
        )}
        <div style={styles.list}>
          {filtered.map((song) => (
            <SongRow
              key={song.songId}
              song={song}
              score={hasPlayer ? scoreMap.get(song.songId) : undefined}
            />
          ))}
        </div>
      </div>
    </div>
  );
}

function SongRow({ song, score }: { song: Song; score?: PlayerScore }) {
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
      {score ? (
        <ScoreMetadata score={score} />
      ) : null}
    </Link>
  );
}

function ScoreMetadata({ score }: { score: PlayerScore }) {
  const pct =
    score.rank > 0 && (score.totalEntries ?? 0) > 0
      ? Math.min((score.rank / score.totalEntries!) * 100, 100)
      : undefined;
  const isTop5 = pct != null && pct <= 5;
  const rawAcc = score.accuracy ?? 0;
  const accuracy = rawAcc > 0 ? (rawAcc / 10000).toFixed(2) + '%' : undefined;
  const is100FC = score.isFullCombo && accuracy === '100.00%';
  const stars = score.stars ?? 0;
  const isGoldStars = stars >= 6;

  return (
    <div style={styles.scoreMeta}>
      {/* Score */}
      <span style={styles.scoreValue}>{score.score.toLocaleString()}</span>

      {/* Stars */}
      {stars > 0 && (
        <span
          style={{
            ...styles.starsPill,
            ...(isGoldStars ? styles.starsPillGold : {}),
          }}
        >
          {isGoldStars ? '★'.repeat(5) : '★'.repeat(stars)}
        </span>
      )}

      {/* Accuracy / FC */}
      {is100FC ? (
        <span style={styles.fcBadge}>FC</span>
      ) : accuracy ? (
        <span
          style={{
            ...styles.accuracyPill,
            ...(score.isFullCombo ? styles.accuracyPillGold : {}),
          }}
        >
          {accuracy}
        </span>
      ) : null}

      {/* FC badge (when not 100%) */}
      {!is100FC && score.isFullCombo && (
        <span style={styles.fcBadge}>FC</span>
      )}

      {/* Percentile */}
      {pct != null && (
        <span
          style={{
            ...styles.percentilePill,
            ...(isTop5 ? styles.percentilePillGold : {}),
          }}
        >
          Top {Math.max(0.01, pct).toFixed(2)}%
        </span>
      )}
    </div>
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
    minHeight: '100%',
    backgroundColor: Colors.backgroundApp,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
  },
  stickyHeader: {
    position: 'sticky' as const,
    top: 0,
    backgroundColor: Colors.backgroundApp,
    zIndex: 10,
    paddingBottom: Gap.md,
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
  syncBanner: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `${Gap.xl}px ${Gap.section}px`,
    backgroundColor: Colors.accentPurpleDark,
    border: `1px solid ${Colors.borderPrimary}`,
    borderRadius: Radius.lg,
    marginBottom: Gap.md,
  },
  syncSpinner: {
    width: 24,
    height: 24,
    border: '3px solid rgba(255,255,255,0.15)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
    flexShrink: 0,
  },
  syncTitle: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.textPrimary,
    marginBottom: Gap.xs,
  },
  syncSubtitle: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  syncProgressOuter: {
    marginTop: Gap.xs,
    height: 6,
    backgroundColor: 'rgba(255,255,255,0.1)',
    borderRadius: 3,
    overflow: 'hidden',
  },
  syncProgressInner: {
    height: '100%',
    backgroundColor: Colors.accentPurple,
    borderRadius: 3,
    transition: 'width 0.3s ease',
  },
  syncProgressLabel: {
    display: 'flex',
    justifyContent: 'space-between',
    fontSize: Font.xs,
    color: Colors.textSecondary,
    marginBottom: Gap.xs,
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
  instrumentBar: {
    display: 'flex',
    gap: Gap.sm,
    alignItems: 'center',
    flexWrap: 'wrap' as const,
    marginBottom: Gap.md,
  },
  instrumentChip: {
    padding: `${Gap.sm}px ${Gap.xl}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.transparent,
    color: Colors.textTertiary,
    fontSize: Font.sm,
    cursor: 'pointer',
  },
  instrumentChipActive: {
    backgroundColor: Colors.chipSelectedBg,
    color: Colors.accentBlue,
    borderColor: Colors.accentBlue,
  },
  loadingDot: {
    fontSize: Font.xs,
    color: Colors.textMuted,
    marginLeft: Gap.sm,
  },
  scoreMeta: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    flexShrink: 0,
  },
  scoreValue: {
    fontSize: Font.md,
    fontWeight: 700,
    color: Colors.textPrimary,
    fontVariantNumeric: 'tabular-nums',
  },
  starsPill: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
    letterSpacing: -1,
  },
  starsPillGold: {
    color: Colors.gold,
  },
  accuracyPill: {
    fontSize: Font.xs,
    fontWeight: 600,
    color: Colors.textSecondary,
    backgroundColor: 'rgba(255,255,255,0.1)',
    padding: `${Gap.xs}px ${Gap.md}px`,
    borderRadius: Radius.xs,
  },
  accuracyPillGold: {
    color: Colors.gold,
    backgroundColor: Colors.goldBg,
    border: `1px solid ${Colors.goldStroke}`,
  },
  fcBadge: {
    fontSize: Font.xs,
    fontWeight: 700,
    color: Colors.gold,
    backgroundColor: Colors.goldBg,
    border: `1px solid ${Colors.goldStroke}`,
    padding: `${Gap.xs}px ${Gap.md}px`,
    borderRadius: Radius.xs,
  },
  percentilePill: {
    fontSize: Font.xs,
    fontWeight: 600,
    color: Colors.textSecondary,
    backgroundColor: 'rgba(255,255,255,0.1)',
    padding: `${Gap.xs}px ${Gap.md}px`,
    borderRadius: Radius.xs,
  },
  percentilePillGold: {
    color: Colors.gold,
    backgroundColor: Colors.goldBg,
    border: `1px solid ${Colors.goldStroke}`,
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
