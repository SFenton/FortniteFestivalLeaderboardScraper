import { useState, useMemo, useEffect, useCallback } from 'react';
import { Link } from 'react-router-dom';
import { useFestival } from '../contexts/FestivalContext';
import { useSyncStatus } from '../hooks/useSyncStatus';
import { api } from '../api/client';
import type { Song, PlayerScore, PlayerResponse, InstrumentKey } from '../models';
import { INSTRUMENT_KEYS, INSTRUMENT_LABELS } from '../models';
import { Colors, Font, Gap, Radius, Layout, Size, MaxWidth } from '../theme';
import SortModal from '../components/SortModal';
import type { SortDraft } from '../components/SortModal';
import FilterModal from '../components/FilterModal';
import type { FilterDraft } from '../components/FilterModal';
import {
  type SongSortMode,
  type SongSettings,
  defaultSongSettings,
  defaultSongFilters,
  loadSongSettings,
  saveSongSettings,
  isFilterActive,
} from '../components/songSettings';

type Props = {
  accountId?: string;
};

export default function SongsPage({ accountId }: Props) {
  const {
    state: { songs, isLoading, error },
  } = useFestival();
  const [search, setSearch] = useState('');
  const [settings, setSettings] = useState<SongSettings>(loadSongSettings);
  const [instrument, setInstrument] = useState<InstrumentKey>('Solo_Guitar');

  // Sort/Filter modal visibility + drafts
  const [showSort, setShowSort] = useState(false);
  const [showFilter, setShowFilter] = useState(false);
  const [sortDraft, setSortDraft] = useState<SortDraft>(() => ({
    sortMode: settings.sortMode,
    sortAscending: settings.sortAscending,
    metadataOrder: settings.metadataOrder,
    instrumentOrder: settings.instrumentOrder,
  }));
  const [filterDraft, setFilterDraft] = useState<FilterDraft>(() => ({
    ...settings.filters,
    instrumentFilter: null,
  }));

  // Persist settings on change
  useEffect(() => { saveSongSettings(settings); }, [settings]);

  const openSort = () => {
    setSortDraft({
      sortMode: settings.sortMode,
      sortAscending: settings.sortAscending,
      metadataOrder: settings.metadataOrder,
      instrumentOrder: settings.instrumentOrder,
    });
    setShowSort(true);
  };
  const applySort = () => {
    setSettings(s => ({ ...s, ...sortDraft }));
    setShowSort(false);
  };
  const resetSort = () => {
    const d = defaultSongSettings();
    setSortDraft({ sortMode: d.sortMode, sortAscending: d.sortAscending, metadataOrder: d.metadataOrder, instrumentOrder: d.instrumentOrder });
  };

  const openFilter = () => {
    setFilterDraft({ ...settings.filters, instrumentFilter: instrument });
    setShowFilter(true);
  };
  const applyFilter = () => {
    const { instrumentFilter, ...filters } = filterDraft;
    if (instrumentFilter) setInstrument(instrumentFilter);
    setSettings(s => ({ ...s, filters }));
    setShowFilter(false);
  };
  const resetFilter = () => {
    setFilterDraft({ ...defaultSongFilters(), instrumentFilter: null });
  };

  const filtersActive = isFilterActive(settings.filters);
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

  // Build a per-song, per-instrument lookup for filter logic
  const allScoreMap = useMemo(() => {
    if (!playerData) return new Map<string, Map<string, PlayerScore>>();
    const map = new Map<string, Map<string, PlayerScore>>();
    for (const s of playerData.scores) {
      let byInst = map.get(s.songId);
      if (!byInst) {
        byInst = new Map();
        map.set(s.songId, byInst);
      }
      byInst.set(s.instrument, s);
    }
    return map;
  }, [playerData]);

  const PAD_INSTRUMENTS: InstrumentKey[] = ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals'];
  const PRO_INSTRUMENTS: InstrumentKey[] = ['Solo_PeripheralGuitar', 'Solo_PeripheralBass'];

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

    // Apply missing-score/FC filters (only when player data is loaded)
    const f = settings.filters;
    if (allScoreMap.size > 0) {
      if (f.missingPadScores) {
        list = list.filter(s => {
          const byInst = allScoreMap.get(s.songId);
          return PAD_INSTRUMENTS.some(inst => !byInst?.get(inst)?.score);
        });
      }
      if (f.missingPadFCs) {
        list = list.filter(s => {
          const byInst = allScoreMap.get(s.songId);
          return PAD_INSTRUMENTS.some(inst => !byInst?.get(inst)?.isFullCombo);
        });
      }
      if (f.missingProScores) {
        list = list.filter(s => {
          const byInst = allScoreMap.get(s.songId);
          return PRO_INSTRUMENTS.some(inst => !byInst?.get(inst)?.score);
        });
      }
      if (f.missingProFCs) {
        list = list.filter(s => {
          const byInst = allScoreMap.get(s.songId);
          return PRO_INSTRUMENTS.some(inst => !byInst?.get(inst)?.isFullCombo);
        });
      }

      // Season filter — only include songs where the score's season is enabled
      const seasonKeys = Object.keys(f.seasonFilter);
      if (seasonKeys.length > 0 && seasonKeys.some(k => f.seasonFilter[Number(k)] === false)) {
        list = list.filter(s => {
          const score = scoreMap.get(s.songId);
          const season = score?.season ?? 0;
          return f.seasonFilter[season] !== false;
        });
      }

      // Percentile filter — only include songs whose percentile falls in an enabled bracket
      const pctKeys = Object.keys(f.percentileFilter);
      if (pctKeys.length > 0 && pctKeys.some(k => f.percentileFilter[Number(k)] === false)) {
        list = list.filter(s => {
          const score = scoreMap.get(s.songId);
          if (!score) return f.percentileFilter[0] !== false;
          // Compute percentile from rank/totalEntries, same as the display
          const pct = score.rank > 0 && (score.totalEntries ?? 0) > 0
            ? Math.min((score.rank / score.totalEntries!) * 100, 100)
            : undefined;
          if (pct == null) return f.percentileFilter[0] !== false;
          // Find the smallest threshold >= pct
          const thresholds = [1,2,3,4,5,10,15,20,25,30,40,50,60,70,80,90,100];
          const bracket = thresholds.find(t => pct <= t) ?? 100;
          return f.percentileFilter[bracket] !== false;
        });
      }

      // Stars filter
      const starKeys = Object.keys(f.starsFilter);
      if (starKeys.length > 0 && starKeys.some(k => f.starsFilter[Number(k)] === false)) {
        list = list.filter(s => {
          const score = scoreMap.get(s.songId);
          const stars = score?.stars ?? 0;
          return f.starsFilter[stars] !== false;
        });
      }

      // Difficulty filter
      const diffKeys = Object.keys(f.difficultyFilter);
      if (diffKeys.length > 0 && diffKeys.some(k => f.difficultyFilter[Number(k)] === false)) {
        list = list.filter(s => {
          const diff = (s as any).difficulty ?? 0;
          return f.difficultyFilter[diff] !== false;
        });
      }
    }

    const dir = settings.sortAscending ? 1 : -1;
    return list.slice().sort((a, b) => {
      const mode = settings.sortMode;
      let cmp = 0;
      switch (mode) {
        case 'title':
          cmp = a.title.localeCompare(b.title);
          break;
        case 'artist':
          cmp = a.artist.localeCompare(b.artist);
          break;
        case 'year':
          cmp = (a.year ?? 0) - (b.year ?? 0);
          break;
        default:
          // For instrument-specific modes we need player data
          if (scoreMap.size > 0) {
            const sa = scoreMap.get(a.songId);
            const sb = scoreMap.get(b.songId);
            cmp = compareByMode(mode, sa, sb);
          } else {
            cmp = a.title.localeCompare(b.title);
          }
      }
      return cmp === 0 ? a.title.localeCompare(b.title) * dir : cmp * dir;
    });
  }, [songs, search, settings.sortMode, settings.sortAscending, settings.filters, scoreMap, allScoreMap]);

  const hasPlayer = !!playerData;

  // Derive available seasons from player scores
  const availableSeasons = useMemo(() => {
    if (!playerData) return [];
    const set = new Set<number>();
    for (const s of playerData.scores) {
      if (s.season != null) set.add(s.season);
    }
    return Array.from(set).sort((a, b) => a - b);
  }, [playerData]);

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
              <button style={styles.iconBtn} onClick={openSort} title="Sort" aria-label="Sort songs">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M11 5h10M11 9h7M11 13h4M3 17l4 4 4-4M7 3v18" /></svg>
              </button>
              {hasPlayer && (
                <button
                  style={{ ...styles.iconBtn, ...(filtersActive ? styles.iconBtnActive : {}) }}
                  onClick={openFilter}
                  title="Filter"
                  aria-label="Filter songs"
                >
                  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" /></svg>
                  {filtersActive && <span style={styles.filterDot} />}
                </button>
              )}
            </div>
          </div>
          {filtersActive && filtered.length !== songs.length && (
            <div style={styles.count}>{filtered.length} of {songs.length} songs</div>
          )}
        </div>
      </div>
      <div style={styles.container}>
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
        {filtered.length === 0 ? (
          <div style={styles.emptyState}>
            <div style={styles.emptyTitle}>No Songs Found</div>
            <div style={styles.emptySubtitle}>
              {filtersActive
                ? 'Try changing your filters to see more songs.'
                : 'The service may be down unexpectedly. Please refresh to try again.'}
            </div>
          </div>
        ) : (
          <div style={styles.list}>
            {filtered.map((song) => (
              <SongRow
                key={song.songId}
                song={song}
                score={hasPlayer ? scoreMap.get(song.songId) : undefined}
              />
            ))}
          </div>
        )}
      </div>

      <SortModal
        visible={showSort}
        draft={sortDraft}
        instrumentFilter={filterDraft.instrumentFilter}
        onChange={setSortDraft}
        onCancel={() => setShowSort(false)}
        onReset={resetSort}
        onApply={applySort}
      />
      <FilterModal
        visible={showFilter}
        draft={filterDraft}
        availableSeasons={availableSeasons}
        onChange={setFilterDraft}
        onCancel={() => setShowFilter(false)}
        onReset={resetFilter}
        onApply={applyFilter}
      />
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

/** Compare two PlayerScores by a given sort mode; undefined scores sort last. */
function compareByMode(mode: SongSortMode, a?: PlayerScore, b?: PlayerScore): number {
  if (!a && !b) return 0;
  if (!a) return 1;
  if (!b) return -1;

  switch (mode) {
    case 'score':
      return a.score - b.score;
    case 'percentage': {
      const pa = a.accuracy ?? 0;
      const pb = b.accuracy ?? 0;
      return pa - pb;
    }
    case 'percentile': {
      // Lower percentile rank = better, so invert for natural ascending
      const pa = a.percentile ?? Infinity;
      const pb = b.percentile ?? Infinity;
      return pa - pb;
    }
    case 'isfc':
      return (a.isFullCombo ? 1 : 0) - (b.isFullCombo ? 1 : 0);
    case 'stars':
      return (a.stars ?? 0) - (b.stars ?? 0);
    case 'seasonachieved':
      return (a.season ?? 0) - (b.season ?? 0);
    case 'hasfc':
      return (a.isFullCombo ? 1 : 0) - (b.isFullCombo ? 1 : 0);
    default:
      return 0;
  }
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
    width: '100%',
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px`,
    boxSizing: 'border-box' as const,
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
  iconBtn: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    position: 'relative' as const,
    width: Size.control,
    height: Size.control,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.transparent,
    color: Colors.textTertiary,
    cursor: 'pointer',
  },
  iconBtnActive: {
    borderColor: Colors.accentBlue,
    color: Colors.accentBlue,
    backgroundColor: Colors.chipSelectedBgSubtle,
  },
  filterDot: {
    position: 'absolute' as const,
    top: 4,
    right: 4,
    width: 6,
    height: 6,
    borderRadius: '50%',
    backgroundColor: Colors.accentBlue,
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
  emptyState: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    justifyContent: 'center',
    height: 'calc(100dvh - 250px)',
    textAlign: 'center' as const,
  },
  emptyTitle: {
    fontSize: Font.xl,
    fontWeight: 700,
    color: Colors.textPrimary,
    marginBottom: Gap.md,
  },
  emptySubtitle: {
    fontSize: Font.md,
    color: Colors.textMuted,
  },
};
