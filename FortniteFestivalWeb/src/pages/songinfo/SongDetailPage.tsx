import { useEffect, useLayoutEffect, useState, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useSearchParams, useNavigationType, useLocation } from 'react-router-dom';
import { useFestival } from '../../contexts/FestivalContext';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { api } from '../../api/client';
import {
  INSTRUMENT_KEYS,
  type ServerInstrumentKey as InstrumentKey,
  type PlayerScore,
  type ServerScoreHistoryEntry as ScoreHistoryEntry,
} from '@festival/core/api/serverTypes';
import { Gap, Layout } from '@festival/theme';
import ArcSpinner from '../../components/common/ArcSpinner';
import s from './SongDetailPage.module.css';
import ScoreHistoryChart from './components/chart/ScoreHistoryChart';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useScoreFilter } from '../../hooks/data/useScoreFilter';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import PathsModal from './components/path/PathsModal';
import SongDetailHeader from './components/SongDetailHeader';
import InstrumentCard from './components/InstrumentCard';

import { songDetailCache } from '../../api/pageCache';
import type { InstrumentData } from '../../api/pageCache';
export { clearSongDetailCache } from '../../api/pageCache';

export default function SongDetailPage() {
  const { t } = useTranslation();
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
  const location = useLocation();
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
        ) as unknown as Record<InstrumentKey, InstrumentData>,
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
        ) as unknown as Record<InstrumentKey, InstrumentData>,
      );
    }
    api.getAllLeaderboards(songId, 10, leewayParam).then((res) => {
      if (cancelled) return;
      const newData = Object.fromEntries(
        INSTRUMENT_KEYS.map((k) => [k, { entries: [], loading: false, error: null }]),
      ) as unknown as Record<InstrumentKey, InstrumentData>;
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
        ) as unknown as Record<InstrumentKey, InstrumentData>,
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

  // Transition: spinner fade-out â†’ staggered content fade-in
  // phase: 'loading' | 'spinnerOut' | 'contentIn'
  const allCached = !!cached && (!player || hasCachedPlayer);
  // Skip animations only when returning to a cached page (not on fresh PUSH).
  // Frozen at mount time â€” the cache getting written mid-lifecycle should not flip this.
  const skipAnimRef = useRef(allCached && navType !== 'PUSH');
  const skipAnim = skipAnimRef.current;
  const { phase } = useLoadPhase(allReady, { skipAnimation: allCached });
  const hasFab = useIsMobile();
  const [headerCollapsed, setHeaderCollapsed] = useState(hasFab || (skipAnim && (cached?.scrollTop ?? 0) > 40));
  const updateScrollMask = useScrollMask(scrollRef, [phase, activeInstruments.length]);
  const userScrolledRef = useRef(false);
  const rushOnScroll = useStaggerRush(scrollRef);
  const handleScroll = useCallback(() => {
    updateScrollMask();
    rushOnScroll();
    userScrolledRef.current = true;
    if (songId) {
      const entry = songDetailCache.get(songId);
      if (entry && scrollRef.current) entry.scrollTop = scrollRef.current.scrollTop;
    }
    if (hasFab) return;
    const el = scrollRef.current;
    if (el) setHeaderCollapsed(el.scrollTop > 40);
  }, [updateScrollMask, rushOnScroll, hasFab, songId]);

  const hasScrolled = useRef(false);

  // Reset scroll tracking when song or instrument changes
  useEffect(() => {
    hasScrolled.current = false;
    userScrolledRef.current = false;
  }, [songId, defaultInstrument]);

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

  // Scroll to the instrument card when arriving with ?instrument= and autoScroll state
  /* v8 ignore start — DOM scroll positioning */
  const autoScroll = !!(location.state as any)?.autoScroll;
  useEffect(() => {
    if (phase !== 'contentIn' || !defaultInstrument || hasScrolled.current || !autoScroll) return;
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
  /* v8 ignore stop */

  if (!songId) {
    return <div className={s.center}>{t('songDetail.songNotFound')}</div>;
  }

  const stagger = (delayMs: number): React.CSSProperties => skipAnim ? {} : ({
    opacity: 0,
    animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards`,
  });
  /* v8 ignore start — animation cleanup */
  const clearAnim = useCallback((e: React.AnimationEvent<HTMLElement>) => {
    const el = e.currentTarget;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  /* v8 ignore stop */

  return (
    <div className={s.page}>
      {song?.albumArt && (
        <div
          className={s.bgImage}
          style={{ backgroundImage: `url(${song.albumArt})` }}
        />
      )}
      <div className={s.bgDim} />
      {phase !== 'contentIn' && (
        <div
          className={s.spinnerOverlay}
          style={phase === 'spinnerOut'
              ? { animation: 'fadeOut 500ms ease-out forwards' }
              : undefined}
        >
          <ArcSpinner />
        </div>
      )}
      {phase === 'contentIn' && (
        <div className={s.stickyHeader} style={{
          padding: hasFab || headerCollapsed
            ? `${Gap.md}px ${Layout.paddingHorizontal}px ${Gap.section}px`
            : `${Layout.paddingTop}px ${Layout.paddingHorizontal}px ${Gap.section}px`,
        }}>
          <div style={stagger(150)} onAnimationEnd={clearAnim}>
            <SongDetailHeader song={song} songId={songId} collapsed={!!(hasFab || headerCollapsed)} noTransition={hasFab} onOpenPaths={() => setPathsOpen(true)} />
          </div>
        </div>
      )}
      <div ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
      {phase === 'contentIn' && (
        <div className={s.container} style={hasFab ? { paddingBottom: 96 } : undefined}>
          {player && scoreHistoryReady && filteredScoreHistory.length > 0 && (
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
          <div className={s.instrumentGrid}>
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

