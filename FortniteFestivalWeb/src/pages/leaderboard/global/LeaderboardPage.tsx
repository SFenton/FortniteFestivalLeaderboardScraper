/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useRef, useState, useCallback, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useSearchParams, useLocation, useNavigate } from 'react-router-dom';
import { useFestival } from '../../../contexts/FestivalContext';
import { usePlayerData } from '../../../contexts/PlayerDataContext';
import { api } from '../../../api/client';
import {
  type ServerInstrumentKey as InstrumentKey,
  type LeaderboardEntry as LeaderboardEntryType,
} from '@festival/core/api/serverTypes';
import { LoadPhase } from '@festival/core';
import SongInfoHeader from '../../../components/songs/headers/SongInfoHeader';
import { LeaderboardEntry } from './components/LeaderboardEntry';
import { PaginatedLeaderboard } from '../../../components/leaderboard/PaginatedLeaderboard';
import { QUERY_SHOW_ACCURACY, QUERY_SHOW_SEASON, QUERY_SHOW_STARS, Layout, MaxWidth, Position, CssValue, padding, FADE_DURATION, STAGGER_INTERVAL } from '@festival/theme';
import { clearStaggerStyle } from '../../../hooks/ui/useStaggerStyle';
import { useScrollContainer } from '../../../contexts/ScrollContainerContext';
import { PageMessage } from '../../PageMessage';
import { useIsMobile, useIsMobileChrome } from '../../../hooks/ui/useIsMobile';
import { useScoreFilter } from '../../../hooks/data/useScoreFilter';
import { useMediaQuery } from '../../../hooks/ui/useMediaQuery';
import Page, { PageBackground } from '../../Page';

const PAGE_SIZE = 25;

import { leaderboardCache } from '../../../api/pageCache';
export { clearLeaderboardCache } from '../../../api/pageCache';

export default function LeaderboardPage() {
  const { t } = useTranslation();
  const { songId, instrument } = useParams<{
    songId: string;
    instrument: string;
  }>();
  const location = useLocation();
  const navigate = useNavigate();
  const {
    state: { songs },
  } = useFestival();

  const song = songs.find((s) => s.songId === songId);
  const instKey = instrument as InstrumentKey;

  const cacheKey = `${songId}:${instKey}`;
  const cached = leaderboardCache.get(cacheKey);
  const hasCached = !!cached;
  // Skip all animations when data is already cached (return visit to this leaderboard)
  const skipAllAnim = hasCached;

  const showAccuracy = useMediaQuery(QUERY_SHOW_ACCURACY);
  const showSeason = useMediaQuery(QUERY_SHOW_SEASON);
  const showStars = useMediaQuery(QUERY_SHOW_STARS);
  const isMobile = !showAccuracy;
  const isNarrow = useIsMobile();
  const hasFab = useIsMobileChrome();

  const [searchParams, setSearchParams] = useSearchParams();

  const { playerData } = usePlayerData();
  const playerScore = useMemo(() => {
    if (!playerData || !songId) return null;
    return playerData.scores.find(
      (s) => s.songId === songId && s.instrument === instKey,
    ) ?? null;
  }, [playerData, songId, instKey]);

  const [entries, setEntries] = useState<LeaderboardEntryType[]>(hasCached ? cached.entries : []);
  const [totalEntries, setTotalEntries] = useState(hasCached ? cached.totalEntries : 0);
  const [localEntries, setLocalEntries] = useState(hasCached ? cached.localEntries : 0);
  const [page, setPage] = useState(hasCached ? cached.page : 0);
  const [loading, setLoading] = useState(!hasCached);
  const [error, setError] = useState<string | null>(null);
  const { isScoreValid, leewayParam } = useScoreFilter();
  const playerRowRef = useRef<HTMLAnchorElement | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const [headerCollapsed, setHeaderCollapsed] = useState(isNarrow);
  const headerPinned = useRef(false);
  const [loadPhase, setLoadPhase] = useState<LoadPhase>(hasCached ? LoadPhase.ContentIn : LoadPhase.Loading);
  // Tracks: 'first' = initial load (stagger everything), 'paginate' = page change (stagger rows only), 'cached' = from cache (no stagger)
  const [animMode, setAnimMode] = useState<'first' | 'paginate' | 'cached'>(skipAllAnim ? 'cached' : 'first');
  const userScrolledRef = useRef(false);

  const staggerRushRef = useRef<(() => void) | undefined>(undefined);
  const resetRush = useCallback(() => staggerRushRef.current?.(), []);
  const scrollContainerRef = useScrollContainer();

  // Cache scroll position + track user interaction (header collapse handled by Page's headerCollapse prop)
  useEffect(() => {
    const scrollEl = scrollContainerRef.current;
    if (!scrollEl) return;
    const onScroll = () => {
      userScrolledRef.current = true;
      const entry = leaderboardCache.get(cacheKey);
      if (entry) entry.scrollTop = scrollEl.scrollTop;
      // When pinned during pagination, unpin once scroll passes threshold
      if (headerPinned.current && scrollEl.scrollTop > 40) {
        headerPinned.current = false;
      }
    };
    scrollEl.addEventListener('scroll', onScroll, { passive: true });
    return () => scrollEl.removeEventListener('scroll', onScroll);
  }, [cacheKey, scrollContainerRef]);

  // Header collapse callback â€” respects pinning (pinned pages keep their state until unpinned)
  const handleHeaderCollapse = useCallback((collapsed: boolean) => {
    if (headerPinned.current) return;
    setHeaderCollapsed(collapsed);
  }, []);

  const totalPages = Math.max(1, Math.ceil(localEntries / PAGE_SIZE));
  const hasPlayerFooter = !!(playerScore && playerData && songId && isScoreValid(songId, instKey, playerScore.score));

  const fetchPage = useCallback(
    async (pageNum: number, mode: 'first' | 'paginate' = 'paginate') => {
      if (!songId || !instrument) return;
      setAnimMode(mode);
      if (mode === 'paginate') {
        scrollContainerRef.current?.scrollTo(0, 0);
        resetRush();
        headerPinned.current = true;
        userScrolledRef.current = false;
      }
      setLoading(true);
      setError(null);
      try {
        const res = await api.getLeaderboard(
          songId,
          instKey,
          PAGE_SIZE,
          pageNum * PAGE_SIZE,
          leewayParam,
        );
        setEntries(res.entries);
        setTotalEntries(res.totalEntries);
        setLocalEntries(res.localEntries ?? res.totalEntries);
        setPage(pageNum);
      } catch (e) {
        setError(e instanceof Error ? e.message : t('leaderboard.failedToLoad'));
      } finally {
        setLoading(false);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps -- t is stable i18n fn
    [songId, instrument, instKey, leewayParam],
  );

  // Restore scroll position when returning from cache
  /* v8 ignore start â€” scroll restoration: scrollTop DOM API */
  useEffect(() => {
    if (!skipAllAnim || !cached) return;
    if (cached.scrollTop > 0) {
      scrollContainerRef.current?.scrollTo(0, cached.scrollTop);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps -- mount-only scroll restore
  }, []);
  /* v8 ignore stop */

  useEffect(() => {
    // Skip fetch if the leaderboard is already cached (return visit)
    if (leaderboardCache.has(cacheKey)) return;
    const pageParam = parseInt(searchParams.get('page') ?? '', 10);
    const startPage = !isNaN(pageParam) && pageParam >= 1 ? pageParam - 1 : 0;
    void fetchPage(startPage, 'first');
  // eslint-disable-next-line react-hooks/exhaustive-deps -- intentionally skip searchParams
  }, [fetchPage, cacheKey]);

  // Write cache whenever data changes
  useEffect(() => {
    if (loading || error || !songId) return;
    leaderboardCache.set(cacheKey, {
      entries,
      totalEntries,
      localEntries,
      page,
      /* v8 ignore next -- scrollTop: DOM scroll API */
      scrollTop: scrollContainerRef.current?.scrollTop ?? 0,
    });
  }, [loading, error, entries, totalEntries, localEntries, page, songId, cacheKey]);

  // Spinner Ã¢â€ â€™ staggered-content transition
  const hasLoadedOnce = useRef(hasCached);
  const loadPhaseRef = useRef(loadPhase);
  loadPhaseRef.current = loadPhase;
  const hasShownContentRef = useRef(loadPhase === LoadPhase.ContentIn);
  useEffect(() => {
    if (loading || error) {
      setLoadPhase(LoadPhase.Loading);
      // Pin header state and scroll to top so new content staggers from top
      headerPinned.current = true;
      userScrolledRef.current = false;
      scrollContainerRef.current?.scrollTo(0, 0);
      return;
    }
    if (loadPhaseRef.current === LoadPhase.ContentIn) {
      hasShownContentRef.current = true;
      return;
    }
    setLoadPhase(LoadPhase.SpinnerOut);
    let retireId: ReturnType<typeof setTimeout>;
    const id = setTimeout(() => {
      resetRush();
      setLoadPhase(LoadPhase.ContentIn);
      hasShownContentRef.current = true;
      // On initial load, let header be expanded and unpin immediately.
      // On pagination, keep pinned Ã¢â‚¬â€ scroll handler will unpin once past threshold.
      /* v8 ignore start -- animation timing: initial load vs pagination */
      if (!hasLoadedOnce.current) {
        hasLoadedOnce.current = true;
        headerPinned.current = false;
        if (!isNarrow) setHeaderCollapsed(false);
      }
      /* v8 ignore stop */
      // Retire header animation mode so future re-renders don't
      // re-apply the fadeInUp to the header.
      retireId = setTimeout(() => setAnimMode('cached'), FADE_DURATION + 100);
    }, 150);
    return () => { clearTimeout(id); clearTimeout(retireId); };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- animation sequence, intentionally omits isNarrow
  }, [loading, error]);

  /* v8 ignore start â€” navToPlayer auto-scroll */
  useEffect(() => {
    if (loadPhase !== LoadPhase.ContentIn || !searchParams.get('navToPlayer')) return;
    const playerIndex = playerData ? entries.findIndex(e => e.accountId === playerData.accountId) : -1;
    if (playerIndex < 0) {
      searchParams.delete('navToPlayer');
      setSearchParams(searchParams, { replace: true });
      return;
    }
    // Wait for the player's row stagger animation to finish, then scroll to it
    const scrollDelay = (playerIndex + 1) * STAGGER_INTERVAL + 300;
    const id = setTimeout(() => {
      if (userScrolledRef.current) return;
      playerRowRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      searchParams.delete('navToPlayer');
      setSearchParams(searchParams, { replace: true });
    }, scrollDelay);
    return () => clearTimeout(id);
  }, [loadPhase, entries, playerData, searchParams, setSearchParams]);
  /* v8 ignore stop */

  const scoreWidth = useMemo(() => {
    const maxLen = Math.max(
      ...entries.map((e) => e.score.toLocaleString().length),
      1,
    );
    return `${maxLen}ch`;
  }, [entries]);

  if (!songId || !instrument) {
    return <PageMessage>{t('leaderboard.notFound')}</PageMessage>;
  }

  const startRank = page * PAGE_SIZE;

  const headerStagger: CSSProperties | undefined = isNarrow || animMode === 'cached' || animMode === 'paginate'
    ? undefined
    : loadPhase === LoadPhase.ContentIn
      ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out forwards` }
      : { opacity: 0 };

  return (
    <Page
      scrollRef={scrollRef}
      scrollDeps={[loadPhase, entries.length]}
      staggerRushRef={staggerRushRef}
      headerCollapse={{ disabled: isNarrow, onCollapse: handleHeaderCollapse }}
      containerStyle={lbStyles.container}
      fabSpacer="none"
      background={<PageBackground src={song?.albumArt} />}
      before={
        <div style={headerStagger} onAnimationEnd={clearStaggerStyle}>
          <SongInfoHeader
            song={song}
            songId={songId!}
            collapsed={!!(isNarrow || headerCollapsed)}
            instrument={instKey}
            animate={!isNarrow}
            hideBackground
          />
        </div>
      }
    >
        {error && <PageMessage error>{error}</PageMessage>}

        {!error && (
          <>
            {/* v8 ignore start — entry rendering via shared component */}
              <PaginatedLeaderboard<LeaderboardEntryType>
                entries={entries}
                page={page + 1}
                totalPages={totalPages}
                onGoToPage={(p) => void fetchPage(p - 1)}
                entryKey={(e) => e.accountId}
                isPlayerEntry={(e) => playerData?.accountId === e.accountId}
                renderRow={(e, i) => (
                  <LeaderboardEntry
                    rank={e.rank ?? startRank + i + 1}
                    displayName={e.displayName || t('common.unknownUser')}
                    score={e.score}
                    season={e.season}
                    accuracy={e.accuracy}
                    isFullCombo={!!e.isFullCombo}
                    stars={e.stars}
                    isPlayer={playerData?.accountId === e.accountId}
                    showSeason={showSeason}
                    showAccuracy={showAccuracy}
                    showStars={showStars}
                    scoreWidth={scoreWidth}
                  />
                )}
                entryLinkTo={(e, isPlayer) => isPlayer ? '/statistics' : `/player/${e.accountId}`}
                linkState={{ backTo: location.pathname }}
                playerRowRef={playerRowRef}
                hasPlayerFooter={hasPlayerFooter}
                renderPlayerFooter={({ className, style }) => (
                  <div onClick={() => navigate('/statistics')} role="button" tabIndex={0}>
                    <div className={className} style={{ ...style, cursor: 'pointer' }}>
                      <LeaderboardEntry
                        rank={playerScore!.rank}
                        displayName={playerData!.displayName}
                        score={playerScore!.score}
                        season={playerScore!.season}
                        accuracy={playerScore!.accuracy}
                        isFullCombo={!!playerScore!.isFullCombo}
                        stars={playerScore!.stars}
                        isPlayer
                        showSeason={showSeason}
                        showAccuracy={showAccuracy}
                        showStars={showStars}
                        scoreWidth={scoreWidth}
                      />
                    </div>
                  </div>
                )}
                loading={loading}
                cached={animMode === 'cached'}
                isMobile={isMobile}
                hasFab={hasFab}
                error={!!error}
                emptyMessage={t('leaderboard.noEntriesOnPage')}
                staggerRushRef={staggerRushRef}
              />
            {/* v8 ignore stop */}
          </>
        )}
    </Page>
  );
}

/* â”€â”€ Static styles â”€â”€ */

const lbStyles = {
  container: {
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    width: CssValue.full,
    padding: padding(0, Layout.paddingHorizontal),
    position: Position.relative,
    zIndex: 1,
  } as CSSProperties,
};
