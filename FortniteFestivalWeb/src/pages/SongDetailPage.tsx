import { useEffect, useLayoutEffect, useState, useCallback, useRef, useMemo } from 'react';
import { useParams, Link, useNavigate, useSearchParams, useNavigationType } from 'react-router-dom';
import { useFestival } from '../contexts/FestivalContext';
import { useTrackedPlayer } from '../hooks/useTrackedPlayer';
import { api } from '../api/client';
import {
  INSTRUMENT_KEYS,
  INSTRUMENT_LABELS,
  type Song,
  type InstrumentKey,
  type LeaderboardEntry,
  type PlayerScore,
  type ScoreHistoryEntry,
} from '../models';
import { Colors, Font, Gap, Radius, Layout, MaxWidth, goldOutlineSkew, frostedCard } from '../theme';
import SeasonPill from '../components/SeasonPill';
import ScoreHistoryChart from '../components/ScoreHistoryChart';
import { InstrumentIcon } from '../components/InstrumentIcons';
import { useSettings, visibleInstruments } from '../contexts/SettingsContext';
import { useScrollMask } from '../hooks/useScrollMask';
import { useIsMobile } from '../hooks/useIsMobile';
import { IoFlash } from 'react-icons/io5';
import { useFabSearch } from '../contexts/FabSearchContext';
import { useScoreFilter } from '../hooks/useScoreFilter';
import PathsModal from '../components/PathsModal';

function accuracyColor(pct: number): string {
  const t = Math.min(Math.max(pct / 100, 0), 1);
  const r = Math.round(220 * (1 - t) + 46 * t);
  const g = Math.round(40 * (1 - t) + 204 * t);
  const b = Math.round(40 * (1 - t) + 113 * t);
  return `rgb(${r},${g},${b})`;
}

type InstrumentData = {
  entries: LeaderboardEntry[];
  loading: boolean;
  error: string | null;
};

type SongDetailCache = {
  instrumentData: Record<InstrumentKey, InstrumentData>;
  playerScores: PlayerScore[];
  scoreHistory: ScoreHistoryEntry[];
  accountId: string | undefined;
  scrollTop: number;
};
const songDetailCache = new Map<string, SongDetailCache>();

export function clearSongDetailCache() {
  songDetailCache.clear();
}

export default function SongDetailPage() {
  const { songId } = useParams<{ songId: string }>();
  const [searchParams] = useSearchParams();
  const defaultInstrument = (searchParams.get('instrument') as InstrumentKey) || undefined;
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
  const {
    state: { songs },
  } = useFestival();
  const { player } = useTrackedPlayer();
  const { settings } = useSettings();
  const activeInstruments = visibleInstruments(settings);
  const fabSearch = useFabSearch();
  const { filterPlayerScores, filterHistory: filterScoreHistory, leewayParam } = useScoreFilter();
  const [pathsOpen, setPathsOpen] = useState(false);

  const navType = useNavigationType();
  const cached = songId ? songDetailCache.get(songId) : undefined;
  const hasCachedPlayer = cached && cached.accountId === player?.accountId;

  const [playerScores, setPlayerScores] = useState<PlayerScore[]>(hasCachedPlayer ? cached.playerScores : []);
  const [playerScoresReady, setPlayerScoresReady] = useState(!!hasCachedPlayer);
  const [scoreHistory, setScoreHistory] = useState<ScoreHistoryEntry[]>(hasCachedPlayer ? cached.scoreHistory : []);
  const [scoreHistoryReady, setScoreHistoryReady] = useState(!!hasCachedPlayer);
  const [instrumentData, setInstrumentData] = useState<Record<InstrumentKey, InstrumentData>>(
    () => cached
      ? cached.instrumentData
      : Object.fromEntries(
          INSTRUMENT_KEYS.map((k) => [k, { entries: [], loading: true, error: null }]),
        ) as Record<InstrumentKey, InstrumentData>,
  );

  // Track whether the component mounted with cached data so effects can skip the initial fetch.
  // After the first render cycle, clear the flag so future prop changes (e.g. player swap) refetch.
  // This must be declared AFTER the fetch effects so it runs last in the effect order.
  // The cache is only fully valid when player-specific data also matches (or no player is selected).
  const mountedWithCacheRef = useRef(!!cached && (!player || !!hasCachedPlayer));

  // Register openPaths for the FAB
  useEffect(() => {
    fabSearch.registerSongDetailActions({ openPaths: () => setPathsOpen(true) });
  }, [fabSearch]);

  const song = songs.find((s) => s.songId === songId);
  const scrollRef = useRef<HTMLDivElement>(null);

  // Fetch player scores
  useEffect(() => {
    if (mountedWithCacheRef.current) return;
    if (!player || !songId) {
      setPlayerScores([]);
      setPlayerScoresReady(true);
      return;
    }
    if (!hasCachedPlayer) setPlayerScoresReady(false);
    let cancelled = false;
    api.getPlayer(player.accountId, songId).then((res) => {
      if (!cancelled) setPlayerScores(res.scores);
    }).catch(() => {
      if (!cancelled) setPlayerScores([]);
    }).finally(() => {
      if (!cancelled) setPlayerScoresReady(true);
    });
    return () => { cancelled = true; };
  }, [player, songId]);

  // Fetch score history
  useEffect(() => {
    if (mountedWithCacheRef.current) return;
    if (!player || !songId) {
      setScoreHistory([]);
      setScoreHistoryReady(true);
      return;
    }
    if (!hasCachedPlayer) setScoreHistoryReady(false);
    let cancelled = false;
    api.getPlayerHistory(player.accountId, songId).then((res) => {
      if (!cancelled) setScoreHistory(res.history);
    }).catch(() => {
      if (!cancelled) setScoreHistory([]);
    }).finally(() => {
      if (!cancelled) setScoreHistoryReady(true);
    });
    return () => { cancelled = true; };
  }, [player, songId]);

  // Fetch all instrument leaderboards in a single request
  useEffect(() => {
    if (mountedWithCacheRef.current) return;
    if (!songId) return;
    let cancelled = false;
    if (!cached) {
      setInstrumentData(
        Object.fromEntries(
          INSTRUMENT_KEYS.map((k) => [k, { entries: [], loading: true, error: null }]),
        ) as Record<InstrumentKey, InstrumentData>,
      );
    }
    api.getAllLeaderboards(songId, 10, leewayParam).then((res) => {
      if (cancelled) return;
      const newData = Object.fromEntries(
        INSTRUMENT_KEYS.map((k) => [k, { entries: [], loading: false, error: null }]),
      ) as Record<InstrumentKey, InstrumentData>;
      for (const inst of res.instruments) {
        const key = inst.instrument as InstrumentKey;
        if (key in newData) {
          newData[key] = { entries: inst.entries, loading: false, error: null };
        }
      }
      setInstrumentData(newData);
    }).catch((e) => {
      if (cancelled) return;
      const errMsg = e instanceof Error ? e.message : 'Error';
      setInstrumentData(
        Object.fromEntries(
          INSTRUMENT_KEYS.map((k) => [k, { entries: [], loading: false, error: errMsg }]),
        ) as Record<InstrumentKey, InstrumentData>,
      );
    });
    return () => { cancelled = true; };
  }, [songId]);

  // Clear the cache-skip flag after all fetch effects have had a chance to check it.
  // Must be declared AFTER the fetch effects so React runs it last in the effect order.
  useEffect(() => { mountedWithCacheRef.current = false; }, []);

  // No player means no player-specific data to wait for
  const playerDataReady = !player || (playerScoresReady && scoreHistoryReady);
  const instrumentsReady = activeInstruments.every((k) => !instrumentData[k].loading);
  const allReady = playerDataReady && instrumentsReady;

  // Apply invalid score filtering
  const filteredScoreHistory = useMemo(() => {
    if (!songId) return scoreHistory;
    // ScoreHistory has per-instrument entries; filter each against its own instrument
    return scoreHistory.filter(h => {
      const instMap = songs.find(s => s.songId === songId)?.maxScores;
      if (!instMap) return true;
      return filterScoreHistory(songId, h.instrument, [h]).length > 0;
    });
  }, [songId, scoreHistory, filterScoreHistory, songs]);

  const filteredPlayerScores = useMemo(
    () => filterPlayerScores(playerScores),
    [playerScores, filterPlayerScores],
  );

  // Compute a global score width so season pills align across all sections
  const globalScoreWidth = useMemo(() => {
    let maxLen = 1;
    for (const inst of activeInstruments) {
      for (const e of instrumentData[inst].entries) {
        maxLen = Math.max(maxLen, e.score.toLocaleString().length);
      }
    }
    for (const s of filteredPlayerScores) {
      maxLen = Math.max(maxLen, s.score.toLocaleString().length);
    }
    for (const h of filteredScoreHistory) {
      maxLen = Math.max(maxLen, h.newScore.toLocaleString().length);
    }
    return `${maxLen}ch`;
  }, [activeInstruments, instrumentData, filteredPlayerScores, filteredScoreHistory]);

  // Transition: spinner fade-out → staggered content fade-in
  // phase: 'loading' | 'spinnerOut' | 'contentIn'
  const allCached = !!cached && (!player || hasCachedPlayer);
  // Skip animations only when returning to a cached page (not on fresh PUSH).
  // Frozen at mount time — the cache getting written mid-lifecycle should not flip this.
  const skipAnimRef = useRef(allCached && navType !== 'PUSH');
  const skipAnim = skipAnimRef.current;
  const [phase, setPhase] = useState<'loading' | 'spinnerOut' | 'contentIn'>(allCached ? 'contentIn' : 'loading');
  const hasFab = useIsMobile();
  const [headerCollapsed, setHeaderCollapsed] = useState(hasFab || (skipAnim && (cached?.scrollTop ?? 0) > 40));
  const updateScrollMask = useScrollMask(scrollRef, [phase, activeInstruments.length]);
  const userScrolledRef = useRef(false);
  const handleScroll = useCallback(() => {
    updateScrollMask();
    userScrolledRef.current = true;
    if (songId) {
      const entry = songDetailCache.get(songId);
      if (entry && scrollRef.current) entry.scrollTop = scrollRef.current.scrollTop;
    }
    if (hasFab) return;
    const el = scrollRef.current;
    if (el) setHeaderCollapsed(el.scrollTop > 40);
  }, [updateScrollMask, hasFab, songId]);

  const hasScrolled = useRef(false);

  // Reset scroll tracking when song or instrument changes
  useEffect(() => {
    hasScrolled.current = false;
    userScrolledRef.current = false;
  }, [songId, defaultInstrument]);

  useEffect(() => {
    if (!allReady) return;
    if (phase === 'contentIn') return;
    setPhase('spinnerOut');
    const id = setTimeout(() => {
      setPhase('contentIn');
    }, 500);
    return () => {
      clearTimeout(id);
    };
  }, [allReady, phase]);

  // Update cache when data is ready
  useEffect(() => {
    if (!songId || !allReady) return;
    songDetailCache.set(songId, {
      instrumentData,
      playerScores,
      scoreHistory,
      accountId: player?.accountId,
      scrollTop: scrollRef.current?.scrollTop ?? 0,
    });
  }, [allReady, songId, instrumentData, playerScores, scoreHistory, player?.accountId]);

  // Restore scroll position when returning from cache (not on fresh PUSH navigations)
  useLayoutEffect(() => {
    if (navType === 'PUSH' || !allCached || !songId) return;
    const saved = songDetailCache.get(songId);
    if (saved && saved.scrollTop > 0 && scrollRef.current) {
      scrollRef.current.scrollTop = saved.scrollTop;
    }
  }, []);

  // Scroll to the instrument card when arriving with ?instrument=
  useEffect(() => {
    if (phase !== 'contentIn' || !defaultInstrument || hasScrolled.current) return;
    hasScrolled.current = true;
    // Wait for stagger animations to complete before measuring position
    const id = setTimeout(() => {
      if (userScrolledRef.current) return;
      const target = document.getElementById(`player-score-${defaultInstrument}`)
        ?? document.getElementById(`instrument-card-${defaultInstrument}`);
      if (!target) return;
      // Find the scrollable ancestor (e.g. #main-content with overflow: auto)
      let scrollContainer: HTMLElement | null = target.parentElement;
      while (scrollContainer) {
        const style = getComputedStyle(scrollContainer);
        if (
          scrollContainer.scrollHeight > scrollContainer.clientHeight &&
          (style.overflowY === 'auto' || style.overflowY === 'scroll')
        ) break;
        scrollContainer = scrollContainer.parentElement;
      }
      if (!scrollContainer) return;
      const containerRect = scrollContainer.getBoundingClientRect();
      const targetRect = target.getBoundingClientRect();
      const nav = document.querySelector('nav');
      const navHeight = nav ? nav.getBoundingClientRect().height : 0;
      const padding = 24;
      const desiredBottom = containerRect.bottom - navHeight - padding;
      const scrollTop = scrollContainer.scrollTop + targetRect.bottom - desiredBottom;
      scrollContainer.scrollTo({ top: Math.max(0, scrollTop), behavior: 'smooth' });
    }, 1500);
    return () => clearTimeout(id);
  }, [phase, defaultInstrument]);

  if (!songId) {
    return <div style={styles.center}>Song not found</div>;
  }

  const stagger = (delayMs: number): React.CSSProperties => skipAnim ? {} : ({
    opacity: 0,
    animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards`,
  });
  const clearAnim = useCallback((e: React.AnimationEvent<HTMLElement>) => {
    const el = e.currentTarget;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);

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
      {phase !== 'contentIn' && (
        <div
          style={{
            ...styles.spinnerOverlay,
            ...(phase === 'spinnerOut'
              ? { animation: 'fadeOut 500ms ease-out forwards' }
              : {}),
          }}
        >
          <div style={styles.arcSpinner} />
        </div>
      )}
      {phase === 'contentIn' && (
        <div style={{
          ...styles.stickyHeader,
          padding: hasFab || headerCollapsed
            ? `${Gap.md}px ${Layout.paddingHorizontal}px ${Gap.section}px`
            : `${Layout.paddingTop}px ${Layout.paddingHorizontal}px ${Gap.section}px`,
        }}>
          <div style={stagger(150)} onAnimationEnd={clearAnim}>
            <SongHeader song={song} songId={songId} collapsed={hasFab || headerCollapsed} noTransition={hasFab} onOpenPaths={() => setPathsOpen(true)} />
          </div>
        </div>
      )}
      <div ref={scrollRef} onScroll={handleScroll} style={styles.scrollArea}>
      {phase === 'contentIn' && (
        <div style={{ ...styles.container, ...(hasFab ? { paddingBottom: 96 } : {}) }}>
          {player && (
            <div style={{ ...stagger(300), marginBottom: Gap.section }} onAnimationEnd={clearAnim}>
              <ScoreHistoryChart
                songId={songId}
                accountId={player.accountId}
                playerName={player.displayName}
                defaultInstrument={defaultInstrument}
                history={filteredScoreHistory}
                visibleInstruments={activeInstruments}
                skipAnimation={skipAnim}
                scoreWidth={globalScoreWidth}
              />
            </div>
          )}
          <div style={styles.instrumentGrid}>
            {activeInstruments.map((inst, idx) => {
              const rowIndex = Math.floor(idx / 2);
              const baseDelay = 450 + rowIndex * 150;
              return (
                  <div key={inst} id={`instrument-card-${inst}`}>
                    <InstrumentCard
                      songId={songId}
                      instrument={inst}
                      baseDelay={baseDelay}
                      windowWidth={windowWidth}
                      playerScore={filteredPlayerScores.find((s) => s.instrument === inst)}
                      playerName={player?.displayName}
                      playerAccountId={player?.accountId}
                      prefetchedEntries={instrumentData[inst].entries}
                      prefetchedError={instrumentData[inst].error}
                      skipAnimation={skipAnim}
                      scoreWidth={globalScoreWidth}
                    />
                  </div>
              );
            })}
          </div>
        </div>
      )}
      </div>
      {songId && <PathsModal visible={pathsOpen} songId={songId} onClose={() => setPathsOpen(false)} />}
    </div>
  );
}

function SongHeader({
  song,
  songId,
  collapsed,
  noTransition,
  onOpenPaths,
}: {
  song: Song | undefined;
  songId: string;
  collapsed: boolean;
  noTransition?: boolean;
  onOpenPaths: () => void;
}) {
  const isMobile = useIsMobile();
  const artSize = collapsed ? 80 : 120;
  const transition = noTransition ? undefined : 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)';
  return (
    <div style={{ ...styles.header, marginTop: collapsed ? 0 : Gap.xl, transition }}>
      {song?.albumArt ? (
        <img src={song.albumArt} alt="" style={{ ...styles.headerArt, width: artSize, height: artSize, borderRadius: collapsed ? Radius.md : Radius.lg, transition }} />
      ) : (
        <div style={{ ...styles.headerArt, ...styles.artPlaceholder, width: artSize, height: artSize, borderRadius: collapsed ? Radius.md : Radius.lg, transition }} />
      )}
      <div style={{ flex: 1, minWidth: 0 }}>
        <h1 style={{ ...styles.songTitle, marginBottom: collapsed ? Gap.xs : Gap.sm, transition }}>{song?.title ?? songId}</h1>
        <p style={{ ...styles.songArtist, fontSize: collapsed ? Font.md : Font.lg, marginBottom: collapsed ? 0 : Gap.md, transition }}>
          {song?.artist ?? 'Unknown Artist'}
        </p>
      </div>
      {!isMobile && (
        <button
          onClick={onOpenPaths}
          style={styles.viewPathsButton}
        >
          <IoFlash size={16} style={{ marginRight: Gap.md }} />
          View Paths
        </button>
      )}
    </div>
  );
}

function getDifficulty(
  song: Song | undefined,
  instrument: InstrumentKey,
): number | undefined {
  if (!song?.difficulty) return undefined;
  const map: Record<InstrumentKey, keyof NonNullable<Song['difficulty']>> = {
    Solo_Guitar: 'guitar',
    Solo_Bass: 'bass',
    Solo_Drums: 'drums',
    Solo_Vocals: 'vocals',
    Solo_PeripheralGuitar: 'proGuitar',
    Solo_PeripheralBass: 'proBass',
  };
  return song.difficulty[map[instrument]];
}

function InstrumentCard({
  songId,
  instrument,
  baseDelay,
  windowWidth,
  playerScore,
  playerName,
  playerAccountId,
  prefetchedEntries,
  prefetchedError,
  skipAnimation,
  scoreWidth,
}: {
  songId: string;
  instrument: InstrumentKey;
  baseDelay: number;
  windowWidth: number;
  playerScore?: PlayerScore;
  playerName?: string;
  playerAccountId?: string;
  prefetchedEntries: LeaderboardEntry[];
  prefetchedError: string | null;
  skipAnimation?: boolean;
  scoreWidth: string;
}) {
  const navigate = useNavigate();

  // 2-col grid: each card gets ~half viewport. Thresholds based on card width.
  const isTwoCol = windowWidth >= 840;
  const cardWidth = isTwoCol ? windowWidth / 2 : windowWidth;
  const showAccuracy = cardWidth >= 420;
  const showSeason = cardWidth >= 520;
  const isMobile = cardWidth < 360;

  // If the tracked player is already in the top entries, highlight them inline
  // instead of showing a separate row at the bottom.
  const playerInTop = !!(playerAccountId && prefetchedEntries.some(
    (e) => e.accountId === playerAccountId,
  ));

  const anim = (delayMs: number): React.CSSProperties => skipAnimation ? {} : ({
    opacity: 0,
    animation: `fadeInUp 300ms ease-out ${delayMs}ms forwards`,
  });
  const clearAnim = (ev: React.AnimationEvent<HTMLElement>) => {
    ev.currentTarget.style.opacity = '';
    ev.currentTarget.style.animation = '';
  };

  return (
    <div style={styles.cardWrapper}>
      <div style={{ ...styles.cardLabel, ...anim(baseDelay) }} onAnimationEnd={clearAnim}>
        <InstrumentIcon instrument={instrument} size={36} />
        <span style={styles.cardTitle}>{INSTRUMENT_LABELS[instrument]}</span>
      </div>
      <div
        style={{
          ...styles.card,
          cursor: 'pointer',
        }}
        onClick={() => {
          navigate(`/songs/${songId}/${instrument}`, { state: { backTo: `/songs/${songId}` } });
        }}
      >
        <div style={styles.cardBody}>
        {prefetchedError && <span style={styles.cardError}>{prefetchedError}</span>}
        {!prefetchedError && prefetchedEntries.length === 0 && (
          <span style={styles.cardMuted}>No entries</span>
        )}
        {!prefetchedError &&
          prefetchedEntries.map((e, i) => {
            const rowStagger = anim(baseDelay + 80 + i * 60);
            const isPlayer = playerInTop && e.accountId === playerAccountId;
            const rowStyle = isPlayer
              ? { ...styles.playerEntryRow, ...(isMobile ? styles.entryRowMobile : {}), ...rowStagger }
              : { ...styles.entryRow, ...(isMobile ? styles.entryRowMobile : {}), ...rowStagger };
            return (
            <Link
              key={e.accountId}
              id={isPlayer ? `player-score-${instrument}` : undefined}
              to={`/player/${e.accountId}`}
              state={{ backTo: `/songs/${songId}` }}
              style={rowStyle}
              onClick={(ev) => ev.stopPropagation()}
              onAnimationEnd={clearAnim}
            >
              <span style={{ ...styles.entryRank, ...(isPlayer ? { fontWeight: 700 } : {}) }}>#{(e.rank ?? i + 1).toLocaleString()}</span>
              <span style={{ ...styles.entryName, ...(isPlayer ? { fontWeight: 700 } : {}) }}>
                {e.displayName ?? e.accountId.slice(0, 8)}
              </span>
              <span style={styles.seasonScoreGroup}>
                {showSeason && e.season != null && (
                  <SeasonPill season={e.season} />
                )}
                <span style={{ ...styles.entryScore, width: scoreWidth }}>
                  {e.score.toLocaleString()}
                </span>
              </span>
              {showAccuracy && (
              <span style={styles.entryAcc}>
                {e.accuracy != null
                  ? (() => {
                      const pct = e.accuracy / 10000;
                      const r1 = pct.toFixed(1);
                      const text = r1.endsWith('.0') ? `${Math.round(pct)}%` : `${r1}%`;
                      return e.isFullCombo
                        ? <span style={styles.fcAccBadge}>{text}</span>
                        : <span style={{ color: accuracyColor(pct) }}>{text}</span>;
                    })()
                  : '—'}
              </span>
              )}
            </Link>
            );
          })}
        {playerName && playerScore && !playerInTop && (() => {
          const playerDelay = baseDelay + 80 + prefetchedEntries.length * 60;
          const playerStagger = anim(playerDelay);
          return (
          <Link
            id={`player-score-${instrument}`}
            to={`/songs/${songId}/${instrument}?page=${Math.floor((playerScore.rank - 1) / 25) + 1}&navToPlayer=true`}
            style={{ ...styles.playerEntryRow, ...(isMobile ? styles.entryRowMobile : {}), ...playerStagger }}
            onClick={(ev) => ev.stopPropagation()}
            onAnimationEnd={clearAnim}
          >
            <span style={{ ...styles.entryRank, fontWeight: 700 }}>#{playerScore.rank.toLocaleString()}</span>
            <span style={{ ...styles.entryName, fontWeight: 700 }}>{playerName}</span>
            <span style={styles.seasonScoreGroup}>
              {showSeason && playerScore.season != null && (
                <SeasonPill season={playerScore.season} />
              )}
              <span style={{ ...styles.entryScore, width: scoreWidth }}>
                {playerScore.score.toLocaleString()}
              </span>
            </span>
            {showAccuracy && (
            <span style={styles.entryAcc}>
              {playerScore.accuracy != null && playerScore.accuracy > 0
                ? (() => {
                    const pct = playerScore.accuracy / 10000;
                    const r1 = pct.toFixed(1);
                    const text = r1.endsWith('.0') ? `${Math.round(pct)}%` : `${r1}%`;
                    return playerScore.isFullCombo
                      ? <span style={styles.fcAccBadge}>{text}</span>
                      : <span style={{ color: accuracyColor(pct) }}>{text}</span>;
                  })()
                : '\u2014'}
            </span>
            )}
          </Link>
          );
        })()}
        {!prefetchedError && prefetchedEntries.length > 0 && (() => {
          const viewAllDelay = baseDelay + 80 + (prefetchedEntries.length + (playerScore && !playerInTop ? 1 : 0)) * 60;
          const viewAllStagger = anim(viewAllDelay);
          return (
            <div
              style={{ ...styles.viewAllButton, ...viewAllStagger }}
              onAnimationEnd={clearAnim}
            >
              View full leaderboard
            </div>
          );
        })()}
      </div>
      </div>
    </div>
  );
}

function DifficultyBadge({ difficulty }: { difficulty: number }) {
  let label: string;
  let bg: string;
  let accent: string;
  if (difficulty <= 1) {
    label = 'Easy';
    bg = Colors.diffEasyBg;
    accent = Colors.diffEasyAccent;
  } else if (difficulty <= 3) {
    label = 'Medium';
    bg = Colors.diffMediumBg;
    accent = Colors.diffMediumAccent;
  } else if (difficulty <= 5) {
    label = 'Hard';
    bg = Colors.diffHardBg;
    accent = Colors.diffHardAccent;
  } else {
    label = 'Expert';
    bg = Colors.diffExpertBg;
    accent = Colors.diffExpertAccent;
  }
  return (
    <span
      style={{
        fontSize: Font.xs,
        padding: `${Gap.xs}px ${Gap.md}px`,
        borderRadius: Radius.xs,
        backgroundColor: bg,
        color: accent,
        fontWeight: 600,
      }}
    >
      {label}
    </span>
  );
}

const styles: Record<string, React.CSSProperties> = {
  page: {
    height: '100%',
    display: 'flex',
    flexDirection: 'column' as const,
    backgroundColor: Colors.backgroundApp,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
    position: 'relative' as const,
    overflow: 'hidden' as const,
  },
  scrollArea: {
    flex: 1,
    overflowY: 'auto' as const,
    position: 'relative' as const,
    zIndex: 1,
  },
  stickyHeader: {
    position: 'relative' as const,
    zIndex: 1,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    width: '100%',
    boxSizing: 'border-box' as const,
    transition: 'padding 300ms cubic-bezier(0.4, 0, 0.2, 1)',
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
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `0 ${Layout.paddingHorizontal}px ${Layout.paddingTop}px`,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.section,
  },
  headerArt: {
    width: 120,
    height: 120,
    borderRadius: Radius.lg,
    objectFit: 'cover' as const,
    flexShrink: 0,
  },
  artPlaceholder: {
    backgroundColor: Colors.purplePlaceholder,
  },
  songTitle: {
    fontSize: Font.title,
    fontWeight: 700,
    marginBottom: Gap.sm,
  },
  songArtist: {
    fontSize: Font.lg,
    color: Colors.textSubtle,
    marginBottom: Gap.md,
  },
  viewPathsButton: {
    ...frostedCard,
    backgroundColor: 'rgba(124,58,237,0.35)',
    border: '1px solid rgba(168,120,255,0.3)',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: `0 ${Gap.section + 8}px 0 ${Gap.section}px`,
    borderRadius: Radius.full,
    color: '#fff',
    fontSize: Font.lg,
    fontWeight: 600,
    textDecoration: 'none',
    cursor: 'pointer',
    flexShrink: 0,
    alignSelf: 'center',
    height: 48,
  } as React.CSSProperties,
  bpmBadge: {
    fontSize: Font.sm,
    color: Colors.textMuted,
    backgroundColor: Colors.surfaceMuted,
    padding: `${Gap.xs}px ${Gap.md}px`,
    borderRadius: Radius.xs,
  },
  instrumentGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(min(420px, 100%), 1fr))',
    gap: `${Gap.section}px ${Gap.md}px`,
  },
  card: {
    display: 'flex',
    flexDirection: 'column' as const,
    height: '100%',
  },
  cardWrapper: {
    display: 'flex',
    flexDirection: 'column' as const,
  },
  cardLabel: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    paddingBottom: Gap.xs,
  },
  cardTitle: {
    fontSize: Font.xl,
    fontWeight: 600,
  },
  cardBody: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.sm,
    flex: 1,
  },
  cardMuted: {
    fontSize: Font.sm,
    color: Colors.textMuted,
  },
  cardError: {
    fontSize: Font.sm,
    color: Colors.statusRed,
  },
  entryRow: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `0 ${Gap.xl}px`,
    height: 48,
    borderRadius: Radius.md,
    ...frostedCard,
    textDecoration: 'none',
    color: 'inherit',
    transition: 'background-color 0.15s',
    fontSize: Font.md,
  },
  entryRowMobile: {
    gap: Gap.md,
    padding: `0 ${Gap.md}px`,
    height: 40,
  },
  entryRank: {
    width: 48,
    flexShrink: 0,
    color: Colors.textPrimary,
    fontSize: Font.md,
  },
  entryName: {
    flex: 1,
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
  },
  entryScore: {
    flexShrink: 0,
    textAlign: 'right' as const,
    fontWeight: 600,
    color: Colors.textPrimary,
    fontVariantNumeric: 'tabular-nums',
  },
  seasonScoreGroup: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.sm,
    flexShrink: 0,
  },
  entryAcc: {
    width: 60,
    flexShrink: 0,
    textAlign: 'center' as const,
    fontWeight: 600,
    color: Colors.accentBlueBright,
    fontVariantNumeric: 'tabular-nums',
  },
  fcAccBadge: {
    ...goldOutlineSkew,
    fontSize: Font.md,
    textAlign: 'center' as const,
  },
  viewAllButton: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: 48,
    borderRadius: Radius.md,
    ...frostedCard,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: 600,
    cursor: 'pointer',
    transition: 'background-color 0.15s',
  },
  playerEntryRow: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `0 ${Gap.xl}px`,
    height: 48,
    borderRadius: Radius.md,
    ...frostedCard,
    backgroundColor: 'rgba(75, 15, 99, 0.75)',
    border: `1px solid rgba(124, 58, 237, 0.5)`,
    textDecoration: 'none',
    color: 'inherit',
    transition: 'background-color 0.15s',
    fontSize: Font.md,
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
  spinnerOverlay: {
    position: 'fixed' as const,
    inset: 0,
    zIndex: 2,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  arcSpinner: {
    width: 48,
    height: 48,
    border: '4px solid rgba(255,255,255,0.10)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  },
};
