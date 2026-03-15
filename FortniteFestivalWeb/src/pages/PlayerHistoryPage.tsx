import { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { useParams } from 'react-router-dom';
import { IoSwapVerticalSharp } from 'react-icons/io5';
import { useFestival } from '../contexts/FestivalContext';
import { useTrackedPlayer } from '../hooks/useTrackedPlayer';
import { useFabSearch } from '../contexts/FabSearchContext';
import { api } from '../api/client';
import {
  INSTRUMENT_LABELS,
  type InstrumentKey,
  type ScoreHistoryEntry,
} from '../models';
import { InstrumentIcon } from '../components/InstrumentIcons';
import SeasonPill from '../components/SeasonPill';
import PlayerScoreSortModal from '../components/PlayerScoreSortModal';
import type { PlayerScoreSortMode, PlayerScoreSortDraft } from '../components/PlayerScoreSortModal';
import { Colors, Font, Gap, Radius, Layout, MaxWidth, Size, goldOutlineSkew, frostedCard } from '../theme';
import { staggerDelay, estimateVisibleCount } from '../utils/stagger';
import { useScrollMask } from '../hooks/useScrollMask';
import { useIsMobile } from '../hooks/useIsMobile';
import { useScoreFilter } from '../hooks/useScoreFilter';
import { IS_IOS, IS_ANDROID, IS_PWA } from '../utils/platform';

function accuracyColor(pct: number): string {
  const t = Math.min(Math.max(pct / 100, 0), 1);
  const r = Math.round(220 * (1 - t) + 46 * t);
  const g = Math.round(40 * (1 - t) + 204 * t);
  const b = Math.round(40 * (1 - t) + 113 * t);
  return `rgb(${r},${g},${b})`;
}

export default function PlayerHistoryPage() {
  const { songId, instrument } = useParams<{
    songId: string;
    instrument: string;
  }>();
  const {
    state: { songs },
  } = useFestival();
  const { player } = useTrackedPlayer();

  const song = songs.find((s) => s.songId === songId);
  const instKey = instrument as InstrumentKey;
  const instLabel = INSTRUMENT_LABELS[instKey] ?? instrument;

  const [windowWidth, setWindowWidth] = useState(window.innerWidth);
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout>;
    const onResize = () => {
      clearTimeout(timer);
      timer = setTimeout(() => setWindowWidth(window.innerWidth), 150);
    };
    window.addEventListener('resize', onResize);
    return () => { clearTimeout(timer); window.removeEventListener('resize', onResize); };
  }, []);
  const showAccuracy = windowWidth >= 420;
  const showSeason = windowWidth >= 520;
  const isMobile = windowWidth < 420;
  const hasFab = useIsMobile();

  const [history, setHistory] = useState<ScoreHistoryEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { filterHistory } = useScoreFilter();
  const scrollRef = useRef<HTMLDivElement>(null);
  const [headerCollapsed, setHeaderCollapsed] = useState(hasFab);
  const [loadPhase, setLoadPhase] = useState<'loading' | 'spinnerOut' | 'contentIn'>('loading');
  const updateScrollMask = useScrollMask(scrollRef, [loadPhase, history.length]);

  // Sort state
  const DEFAULT_SORT: PlayerScoreSortDraft = { sortMode: 'score', sortAscending: false };
  const [sortMode, setSortMode] = useState<PlayerScoreSortMode>('score');
  const [sortAscending, setSortAscending] = useState(false);
  const [showSort, setShowSort] = useState(false);
  const [sortDraft, setSortDraft] = useState<PlayerScoreSortDraft>({ sortMode: 'score', sortAscending: false });
  const [staggerKey, setStaggerKey] = useState(0);

  const openSort = useCallback(() => {
    setSortDraft({ sortMode, sortAscending });
    setShowSort(true);
  }, [sortMode, sortAscending]);
  const applySort = () => {
    setSortMode(sortDraft.sortMode);
    setSortAscending(sortDraft.sortAscending);
    setShowSort(false);
    setStaggerKey(k => k + 1);
  };
  const resetSort = () => {
    setSortDraft({ ...DEFAULT_SORT });
  };

  // Register sort action for FAB
  const fabSearch = useFabSearch();
  const openSortRef = useRef(openSort);
  openSortRef.current = openSort;
  useEffect(() => {
    fabSearch.registerPlayerHistoryActions({ openSort: () => openSortRef.current() });
  }, [fabSearch]);

  const handleScroll = useCallback(() => {
    updateScrollMask();
    if (hasFab) return;
    const el = scrollRef.current;
    if (!el) return;
    setHeaderCollapsed(el.scrollTop > 40);
  }, [updateScrollMask, hasFab]);

  useEffect(() => {
    if (!player || !songId) {
      setLoading(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    api.getPlayerHistory(player.accountId, songId)
      .then((res) => {
        if (!cancelled) {
          const filtered = res.history
            .filter(h => h.instrument === instKey);
          setHistory(filtered);
        }
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load history');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => { cancelled = true; };
  }, [player, songId, instKey]);

  useEffect(() => {
    if (loading || error) {
      setLoadPhase('loading');
      return;
    }
    setLoadPhase('spinnerOut');
    const id = setTimeout(() => {
      setLoadPhase('contentIn');
      if (!hasFab) setHeaderCollapsed(false);
    }, 500);
    return () => clearTimeout(id);
  }, [loading, error]);

  if (!songId || !instrument) {
    return <div style={styles.center}>Not found</div>;
  }

  const filteredHistory = useMemo(
    () => songId ? filterHistory(songId, instKey, history) : history,
    [songId, instKey, history, filterHistory],
  );

  const sortedHistory = useMemo(() => {
    const arr = [...filteredHistory];
    const dir = sortAscending ? 1 : -1;
    arr.sort((a, b) => {
      switch (sortMode) {
        case 'date': {
          const da = a.scoreAchievedAt ?? a.changedAt ?? '';
          const db = b.scoreAchievedAt ?? b.changedAt ?? '';
          return dir * da.localeCompare(db);
        }
        case 'score':
          return dir * (a.newScore - b.newScore);
        case 'accuracy': {
          const cmp = dir * ((a.accuracy ?? 0) - (b.accuracy ?? 0));
          if (cmp !== 0) return cmp;
          // Tiebreakers: FC first, then score, then date
          const fcA = a.isFullCombo ? 1 : 0;
          const fcB = b.isFullCombo ? 1 : 0;
          if (fcA !== fcB) return dir * (fcA - fcB);
          if (a.newScore !== b.newScore) return dir * (a.newScore - b.newScore);
          const da = a.scoreAchievedAt ?? a.changedAt ?? '';
          const db = b.scoreAchievedAt ?? b.changedAt ?? '';
          return dir * da.localeCompare(db);
        }
        case 'season':
          return dir * ((a.season ?? 0) - (b.season ?? 0));
        default:
          return 0;
      }
    });
    return arr;
  }, [filteredHistory, sortMode, sortAscending]);

  const scoreWidth = useMemo(() => {
    const maxLen = Math.max(
      ...filteredHistory.map((h) => h.newScore.toLocaleString().length),
      1,
    );
    return `${maxLen}ch`;
  }, [filteredHistory]);

  const highScoreIndex = useMemo(() => {
    if (sortedHistory.length === 0) return -1;
    let best = 0;
    for (let i = 1; i < sortedHistory.length; i++) {
      if (sortedHistory[i]!.newScore > sortedHistory[best]!.newScore) best = i;
    }
    return best;
  }, [sortedHistory]);

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

        <div style={{
          ...styles.headerBar,
          paddingTop: hasFab || headerCollapsed ? Gap.md : Layout.paddingTop,
          paddingBottom: Gap.section,
          ...(!hasFab ? { transition: 'padding 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
        }}>
          <div style={styles.container}>
            <div style={{
              ...styles.headerContent,
              ...(!hasFab ? { transition: 'margin 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
            }}>
              <div style={styles.headerLeft}>
                {song?.albumArt ? (
                  <img src={song.albumArt} alt="" style={{
                    ...styles.headerArt,
                    width: hasFab || headerCollapsed ? 80 : 120,
                    height: hasFab || headerCollapsed ? 80 : 120,
                    borderRadius: hasFab || headerCollapsed ? Radius.md : Radius.lg,
                    ...(!hasFab ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }} />
                ) : (
                  <div style={{
                    ...styles.headerArt,
                    ...styles.artPlaceholder,
                    width: hasFab || headerCollapsed ? 80 : 120,
                    height: hasFab || headerCollapsed ? 80 : 120,
                    borderRadius: hasFab || headerCollapsed ? Radius.md : Radius.lg,
                    ...(!hasFab ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }} />
                )}
                <div>
                  <h1 style={{
                    ...styles.songTitle,
                    marginBottom: hasFab || headerCollapsed ? Gap.xs : Gap.sm,
                    ...(!hasFab ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }}>{song?.title ?? songId}</h1>
                  <p style={{
                    ...styles.songArtist,
                    fontSize: hasFab || headerCollapsed ? Font.md : Font.lg,
                    ...(!hasFab ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }}>{song?.artist ?? 'Unknown Artist'}</p>
                </div>
              </div>
              <div style={styles.headerRight}>
                {!hasFab && !IS_IOS && !IS_ANDROID && !IS_PWA && (
                  <button style={styles.sortBtn} onClick={openSort} title="Sort" aria-label="Sort player scores">
                    <IoSwapVerticalSharp size={18} />
                  </button>
                )}
                <div style={{
                  width: 56,
                  height: 56,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  transform: hasFab || headerCollapsed ? 'scale(0.857)' : 'scale(1)',
                  ...(!hasFab ? { transition: 'transform 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                }}>
                  <InstrumentIcon instrument={instKey} size={56} />
                </div>
                <span style={styles.instLabel}>{instLabel}</span>
              </div>
            </div>
          </div>
        </div>

      <div ref={scrollRef} onScroll={handleScroll} style={styles.scrollArea}>
        <div style={styles.container}>

        {error && <div style={styles.centerError}>{error}</div>}

        {!error && !player && !loading && (
          <div style={styles.center}>Select a player to view score history</div>
        )}

        {!error && player && (
          <>
            {loadPhase !== 'contentIn' && (
              <div
                style={{
                  ...styles.spinnerContainer,
                  ...(loadPhase === 'spinnerOut'
                    ? { animation: 'fadeOut 500ms ease-out forwards' }
                    : {}),
                }}
              >
                <div style={styles.arcSpinner} />
              </div>
            )}
            {loadPhase === 'contentIn' && (
            <div key={staggerKey} style={{ ...styles.list, ...(hasFab ? { paddingBottom: 96 } : {}) }}>
              {sortedHistory.map((h, i) => {
                const delay = staggerDelay(i, 125, estimateVisibleCount(56));
                const staggerStyle: React.CSSProperties | undefined = delay != null
                  ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${delay}ms forwards` }
                  : undefined;
                const pct = h.accuracy != null ? h.accuracy / 10000 : null;
                const dateStr = new Date(h.scoreAchievedAt ?? h.changedAt)
                  .toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
                const isHighScore = i === highScoreIndex;
                const baseRow = isHighScore ? { ...styles.row, ...styles.rowHighlight } : styles.row;
                const rowStyle = isMobile
                  ? { ...baseRow, gap: Gap.md, padding: `0 ${Gap.md}px`, height: 40 }
                  : baseRow;
                return (
                <div
                  key={`${h.changedAt}-${h.newScore}`}
                  style={{ ...rowStyle, ...staggerStyle }}
                  onAnimationEnd={(ev) => {
                    ev.currentTarget.style.opacity = '';
                    ev.currentTarget.style.animation = '';
                  }}
                >
                  <span style={{ ...styles.colName, ...(isHighScore ? { fontWeight: 700 } : {}) }}>{dateStr}</span>
                  <span style={styles.seasonScoreGroup}>
                    {showSeason && h.season != null && (
                      <SeasonPill season={h.season} />
                    )}
                    <span style={{ ...styles.colScore, width: scoreWidth }}>
                      {h.newScore.toLocaleString()}
                    </span>
                  </span>
                  {showAccuracy && (
                  <span style={styles.colAcc}>
                    {pct != null
                      ? (() => {
                          const r1 = pct.toFixed(1);
                          const text = r1.endsWith('.0') ? `${Math.round(pct)}%` : `${r1}%`;
                          return h.isFullCombo
                            ? <span style={styles.fcAccBadge}>{text}</span>
                            : <span style={{ color: accuracyColor(pct) }}>{text}</span>;
                        })()
                      : '\u2014'}
                  </span>
                  )}
                </div>
                );
              })}
              {sortedHistory.length === 0 && (
                <div style={styles.emptyRow}>No score history for this instrument</div>
              )}
            </div>
            )}
          </>
        )}
      </div>
      </div>

      <PlayerScoreSortModal
        visible={showSort}
        draft={sortDraft}
        savedDraft={{ sortMode, sortAscending }}
        onChange={setSortDraft}
        onCancel={() => setShowSort(false)}
        onReset={resetSort}
        onApply={applySort}
      />
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  page: {
    display: 'flex',
    flexDirection: 'column' as const,
    height: '100%',
    backgroundColor: Colors.backgroundApp,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
    position: 'relative' as const,
  },
  scrollArea: {
    flex: 1,
    overflowY: 'auto' as const,
    position: 'relative' as const,
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
    padding: `0 ${Layout.paddingHorizontal}px`,
  },
  headerBar: {
    position: 'relative' as const,
    zIndex: 2,
    flexShrink: 0,
  },
  headerContent: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  headerLeft: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.section,
    minWidth: 0,
  },
  headerRight: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    flexShrink: 0,
    paddingRight: Gap.md,
  },
  sortBtn: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    position: 'relative' as const,
    width: Size.control,
    height: Size.control,
    borderRadius: Radius.xs,
    ...frostedCard,
    color: Colors.textTertiary,
    cursor: 'pointer',
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
  },
  instLabel: {
    fontSize: Font.xl,
    fontWeight: 600,
  },
  list: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.sm,
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `0 ${Gap.xl}px`,
    height: 48,
    borderRadius: Radius.md,
    ...frostedCard,
    color: 'inherit',
    fontSize: Font.md,
  },
  rowHighlight: {
    backgroundColor: 'rgba(75, 15, 99, 0.75)',
    border: `1px solid rgba(124, 58, 237, 0.5)`,
  },
  emptyRow: {
    padding: `${Gap.xl}px`,
    textAlign: 'center' as const,
    color: Colors.textMuted,
  },
  colName: {
    flex: 1,
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
  },
  colScore: {
    flexShrink: 0,
    textAlign: 'right' as const,
    fontWeight: 600,
    fontSize: Font.lg,
    color: Colors.textPrimary,
    fontVariantNumeric: 'tabular-nums',
  },
  seasonScoreGroup: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    flexShrink: 0,
  },
  colAcc: {
    width: 64,
    flexShrink: 0,
    textAlign: 'center' as const,
    fontWeight: 600,
    fontSize: Font.lg,
    color: Colors.accentBlueBright,
    fontVariantNumeric: 'tabular-nums',
  },
  fcAccBadge: {
    ...goldOutlineSkew,
    fontSize: Font.lg,
    textAlign: 'center' as const,
  },
  spinnerContainer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: 'calc(100vh - 350px)',
  },
  arcSpinner: {
    width: 48,
    height: 48,
    border: '4px solid rgba(255,255,255,0.10)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
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
