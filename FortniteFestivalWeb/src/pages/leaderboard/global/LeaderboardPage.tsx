/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useRef, useState, useCallback, useMemo, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { useParams, Link, useSearchParams, useLocation, useNavigate } from 'react-router-dom';
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
import { Gap, QUERY_SHOW_ACCURACY, QUERY_SHOW_SEASON, QUERY_SHOW_STARS, Colors, Radius, Layout, MaxWidth, Font, Border, Overflow, Position, Display, Align, Justify, BoxSizing, CssValue, CssProp, TextAlign, ZIndex, PointerEvents, flexRow, flexColumn, flexCenter, frostedCard, padding, border, transition, NAV_TRANSITION_MS, FADE_DURATION } from '@festival/theme';
import { clearStaggerStyle } from '../../../hooks/ui/useStaggerStyle';
import ArcSpinner from '../../../components/common/ArcSpinner';
import { useScrollContainer } from '../../../contexts/ScrollContainerContext';
import { PaginationButton } from '../../../components/common/PaginationButton';
import { PageMessage } from '../../PageMessage';
import { staggerDelay, IS_PWA } from '@festival/ui-utils';
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

  // Header collapse callback — respects pinning (pinned pages keep their state until unpinned)
  const handleHeaderCollapse = useCallback((collapsed: boolean) => {
    if (headerPinned.current) return;
    setHeaderCollapsed(collapsed);
  }, []);

  const totalPages = Math.max(1, Math.ceil(localEntries / PAGE_SIZE));
  const hasPagination = totalPages > 1;
  const hasPlayerFooter = !!(playerScore && playerData && songId && isScoreValid(songId, instKey, playerScore.score));

  // Desktop (non-FAB): expand scroll container margin to clear fixed footer
  const showDesktopFixedFooter = !hasFab && (hasPagination || hasPlayerFooter);
  useEffect(() => {
    if (!showDesktopFixedFooter) return;
    const el = scrollContainerRef.current;
    if (!el) return;
    const footerHeight = (hasPagination ? Layout.paginationHeight + Gap.xl : 0) + (hasPlayerFooter ? Layout.entryRowHeight + Gap.xl : 0);
    el.style.marginBottom = `${footerHeight}px`;
    return () => { el.style.marginBottom = ''; };
  }, [showDesktopFixedFooter, hasPagination, hasPlayerFooter, scrollContainerRef]);

  // When FAB + pagination are both visible, expand the scroll container margin
  // beyond the default fabSpacer to ensure content doesn't scroll under the fixed pagination.
  const showMobilePagination = hasFab && hasPagination;
  useEffect(() => {
    if (!showMobilePagination) return;
    const el = scrollContainerRef.current;
    if (!el) return;
    el.style.marginBottom = `${Layout.fabPaddingBottom + Layout.paginationHeight}px`;
    return () => { el.style.marginBottom = ''; };
  }, [showMobilePagination, scrollContainerRef]);

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
  /* v8 ignore start — scroll restoration: scrollTop DOM API */
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

  // Spinner â†’ staggered-content transition
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
      // On pagination, keep pinned â€” scroll handler will unpin once past threshold.
      /* v8 ignore start -- animation timing: initial load vs pagination */
      if (!hasLoadedOnce.current) {
        hasLoadedOnce.current = true;
        headerPinned.current = false;
        if (!isNarrow) setHeaderCollapsed(false);
      }
      /* v8 ignore stop */
      // Retire stagger animations after they've had time to finish so that
      // future re-renders (e.g. from scroll-driven headerCollapsed changes)
      // don't re-apply opacity:0 + animation to rows/pagination/footer.
      const staggerWindow = lastRowDelayRef.current + 400;
      retireId = setTimeout(() => setAnimMode('cached'), staggerWindow);
    }, 150);
    return () => { clearTimeout(id); clearTimeout(retireId); };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- animation sequence, intentionally omits isNarrow
  }, [loading, error]);

  /* v8 ignore start — navToPlayer auto-scroll */
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

  const listRef = useRef<HTMLDivElement>(null);
  const lastRowDelayRef = useRef(0);

  if (!songId || !instrument) {
    return <PageMessage>{t('leaderboard.notFound')}</PageMessage>;
  }

  const startRank = page * PAGE_SIZE;

  // Row = 48px height + Gap.sm gap â‰ˆ 52px effective.
  // scrollRef wraps the scroll viewport and is always mounted (even during spinner
  // phase), so clientHeight is reliable on the first contentIn render â€” unlike
  // listRef which lives inside the contentIn conditional and is null initially.
  const ROW_SLOT = 48 + Gap.sm;
  /* v8 ignore next 2 -- scrollRef.clientHeight: DOM measurement */
  const scrollViewHeight = scrollRef.current?.clientHeight
    ?? Math.max(0, window.innerHeight - (isNarrow ? 120 : 200));
  const maxVisibleRows = Math.min(
    entries.length,
    Math.max(1, Math.ceil(scrollViewHeight / ROW_SLOT)),
  );
  const STAGGER_INTERVAL = 125;
  const lastRowDelay = maxVisibleRows * STAGGER_INTERVAL;
  lastRowDelayRef.current = lastRowDelay;

  const headerStagger: CSSProperties | undefined = isNarrow || animMode === 'cached' || animMode === 'paginate'
    ? undefined
    : loadPhase === LoadPhase.ContentIn
      ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out forwards` }
      : { opacity: 0 };

  return (
    <>
    <Page
      scrollRef={scrollRef}
      scrollDeps={[loadPhase, entries.length]}
      staggerRushRef={staggerRushRef}
      headerCollapse={{ disabled: isNarrow, onCollapse: handleHeaderCollapse }}
      containerStyle={lbStyles.container}
      fabSpacer="fixed"
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
            {loadPhase !== LoadPhase.ContentIn && (
              <div
                style={{ ...lbStyles.spinnerContainer,
                  ...(loadPhase === LoadPhase.SpinnerOut
                    ? { animation: 'fadeOut 150ms ease-out forwards' }
                    : {}),
                }}
              >
                <ArcSpinner />
              </div>
            )}
            {/* v8 ignore start — entry rendering ternaries */}
            {loadPhase === LoadPhase.ContentIn && (
            <div ref={listRef} style={lbStyles.list}>
              {entries.map((e, i) => {
                const isPlayer = playerData?.accountId === e.accountId;
                // Rows stagger on first load and pagination, skip on cache
                const delay = animMode === 'cached' ? null : (staggerDelay(i, STAGGER_INTERVAL, maxVisibleRows) ?? 0);
                const staggerStyle: React.CSSProperties | undefined = delay != null
                  ? { opacity: 0, animation: `fadeInUp 300ms ease-out ${delay}ms forwards` }
                  : undefined;
                const rowStyle = isPlayer ? lbStyles.rowHighlight : lbStyles.row;
                return (
                <Link
                  key={e.accountId}
                  ref={isPlayer ? playerRowRef : undefined}
                  to={isPlayer ? '/statistics' : `/player/${e.accountId}`}
                  state={{ backTo: location.pathname }}
                  style={{ ...rowStyle, ...staggerStyle }}
                  onAnimationEnd={(ev) => {
                    /* v8 ignore start — animation cleanup */
                    const el = ev.currentTarget;
                    el.style.opacity = '';
                    el.style.animation = '';
                    /* v8 ignore stop */
                  }}
                >
                  <LeaderboardEntry
                    rank={e.rank ?? startRank + i + 1}
                    displayName={e.displayName || t('common.unknownUser')}
                    score={e.score}
                    season={e.season}
                    accuracy={e.accuracy}
                    isFullCombo={!!e.isFullCombo}
                    stars={e.stars}
                    isPlayer={isPlayer}
                    showSeason={showSeason}
                    showAccuracy={showAccuracy}
                    showStars={showStars}
                    scoreWidth={scoreWidth}
                  />
                </Link>
                );
              })}
              {entries.length === 0 && (
                <div style={lbStyles.emptyRow}>{t('leaderboard.noEntriesOnPage')}</div>
              )}
            </div>
            )}
            {/* v8 ignore stop */}
          </>
        )}
    </Page>

    {/* v8 ignore start — fixed pagination + player portaled to body */}
    {createPortal(
      <>
        {hasLoadedOnce.current && !error && hasPagination && (() => {
          const paginationStyle = isMobile
            ? lbStyles.paginationMobile
            : lbStyles.pagination;
          return (
        <div style={hasFab ? lbStyles.mobilePagination : lbStyles.desktopPagination}>
          <div className={hasFab ? 'fab-player-footer' : ''} style={paginationStyle}>
            <PaginationButton disabled={page === 0} onClick={() => void fetchPage(0)}>
              {t('leaderboard.first')}
            </PaginationButton>
            <PaginationButton disabled={page === 0} onClick={() => void fetchPage(page - 1)}>
              {t('leaderboard.prev')}
            </PaginationButton>
            <span style={lbStyles.pageInfo}>
              <span style={lbStyles.pageInfoBadge}>{(page + 1).toLocaleString()} / {totalPages.toLocaleString()}</span>
            </span>
            <PaginationButton disabled={page >= totalPages - 1} onClick={() => void fetchPage(page + 1)}>
              {t('leaderboard.next')}
            </PaginationButton>
            <PaginationButton disabled={page >= totalPages - 1} onClick={() => void fetchPage(totalPages - 1)}>
              {t('leaderboard.last')}
            </PaginationButton>
          </div>
        </div>
          );
        })()}
        {hasPlayerFooter && (
        <div style={hasFab ? lbStyles.playerFooterFab : lbStyles.desktopPlayerFooter}>
          <div
            onClick={() => navigate('/statistics')} role="button" tabIndex={0}
          >
            <div className={hasFab ? 'fab-player-footer' : ''} style={{ ...lbStyles.playerFooterRow, cursor: 'pointer' }}>
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
        </div>
        )}
      </>,
      document.body)}
    {/* v8 ignore stop */}
    </>
  );
}

/* ── Static styles ── */

const lbStyles = {
  container: {
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    width: CssValue.full,
    padding: padding(0, Layout.paddingHorizontal),
    position: Position.relative,
    zIndex: 1,
  } as CSSProperties,
  spinnerContainer: {
    ...flexCenter,
    minHeight: 'calc(100vh - 350px)',
  } as CSSProperties,
  list: {
    ...flexColumn,
    gap: Gap.sm,
    overflow: Overflow.hidden,
  } as CSSProperties,
  row: {
    ...frostedCard,
    ...flexRow,
    gap: Gap.xl,
    padding: padding(0, Gap.xl),
    height: Layout.entryRowHeight,
    borderRadius: Radius.md,
    textDecoration: CssValue.none,
    color: CssValue.inherit,
    transition: transition(CssProp.backgroundColor, NAV_TRANSITION_MS),
    fontSize: Font.md,
  } as CSSProperties,
  rowHighlight: {
    ...frostedCard,
    ...flexRow,
    gap: Gap.xl,
    padding: padding(0, Gap.xl),
    height: Layout.entryRowHeight,
    borderRadius: Radius.md,
    textDecoration: CssValue.none,
    color: CssValue.inherit,
    fontSize: Font.md,
    backgroundColor: Colors.purpleHighlight,
    border: border(Border.thin, Colors.purpleHighlightBorder),
  } as CSSProperties,
  emptyRow: {
    padding: Gap.xl,
    textAlign: TextAlign.center,
    color: Colors.textMuted,
  } as CSSProperties,
  pagination: {
    ...flexRow,
    justifyContent: Justify.center,
    gap: Gap.md,
    flexShrink: 0,
    padding: padding(Gap.md, Layout.paddingHorizontal),
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    width: CssValue.full,
    boxSizing: BoxSizing.borderBox,
    position: Position.relative,
    zIndex: 1,
  } as CSSProperties,
  paginationMobile: {
    ...flexRow,
    justifyContent: Justify.between,
    gap: Gap.none,
    flexShrink: 0,
    padding: padding(Gap.md, Layout.paddingHorizontal),
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    width: CssValue.full,
    boxSizing: BoxSizing.borderBox,
    position: Position.relative,
    zIndex: 1,
  } as CSSProperties,
  paginationFab: {
    ...flexRow,
    justifyContent: Justify.center,
    gap: Gap.md,
    flexShrink: 0,
    padding: padding(Gap.md, Layout.paddingHorizontal, Layout.fabPaddingBottom),
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    width: CssValue.full,
    boxSizing: BoxSizing.borderBox,
    position: Position.relative,
    zIndex: 1,
  } as CSSProperties,
  paginationMobileFab: {
    ...flexRow,
    justifyContent: Justify.between,
    gap: Gap.none,
    flexShrink: 0,
    padding: padding(Gap.md, Layout.paddingHorizontal, Layout.fabPaddingBottom),
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    width: CssValue.full,
    boxSizing: BoxSizing.borderBox,
    position: Position.relative,
    zIndex: 1,
  } as CSSProperties,
  pageInfo: {
    textAlign: TextAlign.center,
  } as CSSProperties,
  pageInfoBadge: {
    ...frostedCard,
    display: Display.inlineFlex,
    alignItems: Align.center,
    justifyContent: Justify.center,
    fontSize: Font.sm,
    color: Colors.textSecondary,
    padding: padding(Gap.md, Gap.xl),
    borderRadius: Radius.sm,
    backgroundColor: Colors.backgroundCard,
  } as CSSProperties,
  /* ── Fixed footer styles (portaled to body) ── */
  playerFooterFab: {
    position: Position.fixed,
    bottom: Layout.fabBottom + (Layout.fabSize - Layout.entryRowHeight) / 2,
    left: Gap.none,
    right: Gap.none,
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    padding: padding(0, Layout.paddingHorizontal),
    boxSizing: BoxSizing.borderBox,
    zIndex: ZIndex.popover,
    pointerEvents: PointerEvents.auto,
  } as CSSProperties,
  mobilePagination: {
    position: Position.fixed,
    bottom: Layout.fabBottom + (Layout.fabSize - Layout.entryRowHeight) / 2 + Layout.entryRowHeight + Gap.sm,
    left: Gap.none,
    right: Gap.none,
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    padding: padding(0, Layout.paddingHorizontal),
    boxSizing: BoxSizing.borderBox,
    zIndex: ZIndex.popover,
    pointerEvents: PointerEvents.auto,
  } as CSSProperties,
  desktopPagination: {
    position: Position.fixed,
    bottom: Layout.entryRowHeight + Gap.xl,
    left: Gap.none,
    right: Gap.none,
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    padding: padding(0, Layout.paddingHorizontal),
    boxSizing: BoxSizing.borderBox,
    zIndex: ZIndex.popover,
    pointerEvents: PointerEvents.auto,
  } as CSSProperties,
  desktopPlayerFooter: {
    position: Position.fixed,
    bottom: Gap.none,
    left: Gap.none,
    right: Gap.none,
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    padding: padding(0, Layout.paddingHorizontal),
    boxSizing: BoxSizing.borderBox,
    zIndex: ZIndex.popover,
    pointerEvents: PointerEvents.auto,
  } as CSSProperties,
  playerFooterRow: {
    ...frostedCard,
    ...flexRow,
    gap: Gap.xl,
    height: Layout.entryRowHeight,
    padding: padding(0, Gap.xl),
    borderRadius: Radius.md,
    backgroundColor: Colors.purpleHighlight,
    border: border(Border.thin, Colors.purpleHighlightBorder),
    fontSize: Font.md,
  } as CSSProperties,
};
