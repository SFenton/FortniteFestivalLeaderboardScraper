import { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, Link, useSearchParams, useLocation, useNavigate } from 'react-router-dom';
import { useFestival } from '../contexts/FestivalContext';
import { usePlayerData } from '../contexts/PlayerDataContext';
import { api } from '../api/client';
import {
  INSTRUMENT_LABELS,
  type InstrumentKey,
  type LeaderboardEntry,
} from '../models';
import { InstrumentIcon } from '../components/InstrumentIcons';
import SeasonPill from '../components/SeasonPill';
import { Font, Gap, Radius, Layout } from '@festival/theme';
import s from './LeaderboardPage.module.css';
import { staggerDelay } from '../utils/stagger';
import { useScrollMask } from '../hooks/useScrollMask';
import { useStaggerRush } from '../hooks/useStaggerRush';
import { useIsMobile, useIsMobileChrome } from '../hooks/useIsMobile';
import { useScoreFilter } from '../hooks/useScoreFilter';
import { useMediaQuery } from '../hooks/useMediaQuery';
import { IS_PWA } from '../utils/platform';
import { accuracyColor } from '@festival/core';

const PAGE_SIZE = 25;

type LeaderboardCache = {
  entries: LeaderboardEntry[];
  totalEntries: number;
  localEntries: number;
  page: number;
  scrollTop: number;
};
const leaderboardCache = new Map<string, LeaderboardCache>();

export function clearLeaderboardCache() {
  leaderboardCache.clear();
}

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
  const instLabel = INSTRUMENT_LABELS[instKey] ?? instrument;

  const cacheKey = `${songId}:${instKey}`;
  const cached = leaderboardCache.get(cacheKey);
  const hasCached = !!cached;
  // Skip all animations when data is already cached (return visit to this leaderboard)
  const skipAllAnim = hasCached;

  const showAccuracy = useMediaQuery('(min-width: 420px)');
  const showSeason = useMediaQuery('(min-width: 520px)');
  const showStars = useMediaQuery('(min-width: 768px)');
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

  const [entries, setEntries] = useState<LeaderboardEntry[]>(hasCached ? cached.entries : []);
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
  const [loadPhase, setLoadPhase] = useState<'loading' | 'spinnerOut' | 'contentIn'>(hasCached ? 'contentIn' : 'loading');
  // Tracks: 'first' = initial load (stagger everything), 'paginate' = page change (stagger rows only), 'cached' = from cache (no stagger)
  const [animMode, setAnimMode] = useState<'first' | 'paginate' | 'cached'>(skipAllAnim ? 'cached' : 'first');
  const updateScrollMask = useScrollMask(scrollRef, [loadPhase, entries.length]);
  const userScrolledRef = useRef(false);

  // Save scroll position to cache on scroll
  const handleScrollCache = useCallback(() => {
    const entry = leaderboardCache.get(cacheKey);
    if (entry && scrollRef.current) entry.scrollTop = scrollRef.current.scrollTop;
  }, [cacheKey]);

  const rushOnScroll = useStaggerRush(scrollRef);
  const handleScroll = useCallback(() => {
    updateScrollMask();
    handleScrollCache();
    rushOnScroll();
    userScrolledRef.current = true;
    if (isNarrow) return; // On mobile, header is always collapsed
    const el = scrollRef.current;
    if (!el) return;
    // If pinned (after pagination), only unpin once user scrolls past threshold
    if (headerPinned.current) {
      if (el.scrollTop > 40) headerPinned.current = false;
      return;
    }
    setHeaderCollapsed(el.scrollTop > 40);
  }, [updateScrollMask, handleScrollCache, rushOnScroll, isNarrow]);

  const totalPages = Math.max(1, Math.ceil(localEntries / PAGE_SIZE));

  const fetchPage = useCallback(
    async (pageNum: number, mode: 'first' | 'paginate' = 'paginate') => {
      if (!songId || !instrument) return;
      setAnimMode(mode);
      if (mode === 'paginate') {
        scrollRef.current?.scrollTo(0, 0);
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
    [songId, instrument, instKey, leewayParam],
  );

  // Restore scroll position when returning from cache
  useEffect(() => {
    if (!skipAllAnim || !cached) return;
    if (cached.scrollTop > 0 && scrollRef.current) {
      scrollRef.current.scrollTop = cached.scrollTop;
    }
  }, []);

  useEffect(() => {
    // Skip fetch if the leaderboard is already cached (return visit)
    if (leaderboardCache.has(cacheKey)) return;
    const pageParam = parseInt(searchParams.get('page') ?? '', 10);
    const startPage = !isNaN(pageParam) && pageParam >= 1 ? pageParam - 1 : 0;
    void fetchPage(startPage, 'first');
  }, [fetchPage, cacheKey]);

  // Write cache whenever data changes
  useEffect(() => {
    if (loading || error || !songId) return;
    leaderboardCache.set(cacheKey, {
      entries,
      totalEntries,
      localEntries,
      page,
      scrollTop: scrollRef.current?.scrollTop ?? 0,
    });
  }, [loading, error, entries, totalEntries, localEntries, page, songId, cacheKey]);

  // Spinner → staggered-content transition
  const hasLoadedOnce = useRef(hasCached);
  const loadPhaseRef = useRef(loadPhase);
  loadPhaseRef.current = loadPhase;
  const hasShownContentRef = useRef(loadPhase === 'contentIn');
  useEffect(() => {
    if (loading || error) {
      // Don't hide already-visible content for pagination
      if (hasShownContentRef.current) return;
      setLoadPhase('loading');
      // Pin header state and scroll to top so new content staggers from top
      headerPinned.current = true;
      userScrolledRef.current = false;
      scrollRef.current?.scrollTo(0, 0);
      return;
    }
    if (loadPhaseRef.current === 'contentIn') {
      hasShownContentRef.current = true;
      return;
    }
    setLoadPhase('spinnerOut');
    let retireId: ReturnType<typeof setTimeout>;
    const id = setTimeout(() => {
      setLoadPhase('contentIn');
      hasShownContentRef.current = true;
      // On initial load, let header be expanded and unpin immediately.
      // On pagination, keep pinned — scroll handler will unpin once past threshold.
      if (!hasLoadedOnce.current) {
        hasLoadedOnce.current = true;
        headerPinned.current = false;
        if (!isNarrow) setHeaderCollapsed(false);
      }
      // Retire stagger animations after they've had time to finish so that
      // future re-renders (e.g. from scroll-driven headerCollapsed changes)
      // don't re-apply opacity:0 + animation to rows/pagination/footer.
      const staggerWindow = lastRowDelayRef.current + 400;
      retireId = setTimeout(() => setAnimMode('cached'), staggerWindow);
    }, 150);
    return () => { clearTimeout(id); clearTimeout(retireId); };
  }, [loading, error]);

  useEffect(() => {
    if (loadPhase !== 'contentIn' || !searchParams.get('navToPlayer')) return;
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

  if (!songId || !instrument) {
    return <div className={s.center}>{t('leaderboard.notFound')}</div>;
  }

  const startRank = page * PAGE_SIZE;

  const scoreWidth = useMemo(() => {
    const maxLen = Math.max(
      ...entries.map((e) => e.score.toLocaleString().length),
      1,
    );
    return `${maxLen}ch`;
  }, [entries]);

  // Row = 48px height + Gap.sm gap ≈ 52px effective.
  // scrollRef wraps the scroll viewport and is always mounted (even during spinner
  // phase), so clientHeight is reliable on the first contentIn render — unlike
  // listRef which lives inside the contentIn conditional and is null initially.
  const listRef = useRef<HTMLDivElement>(null);
  const ROW_SLOT = 48 + Gap.sm;
  const scrollViewHeight = scrollRef.current?.clientHeight
    ?? Math.max(0, window.innerHeight - (isNarrow ? 120 : 200));
  const maxVisibleRows = Math.min(
    entries.length,
    Math.max(1, Math.ceil(scrollViewHeight / ROW_SLOT)),
  );
  const STAGGER_INTERVAL = 125;
  const lastRowDelay = maxVisibleRows * STAGGER_INTERVAL;
  const lastRowDelayRef = useRef(lastRowDelay);
  lastRowDelayRef.current = lastRowDelay;

  return (
    <div className={s.page}>
        {song?.albumArt && (
          <div
            className={s.bgImage} style={{
              backgroundImage: `url(${song.albumArt})`,
            }}
          />
        )}
        <div className={s.bgDim} />

        <div className={s.headerBar} style={{
          paddingTop: isNarrow || headerCollapsed ? Gap.md : Layout.paddingTop,
          paddingBottom: Gap.section,
          ...(!isNarrow ? { transition: 'padding 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
        }}>
          <div className={s.container}>
            <div className={s.headerContent} style={{
              ...(!isNarrow ? { transition: 'margin 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
            }}>
              <div className={s.headerLeft}>
                {song?.albumArt ? (
                  <img src={song.albumArt} alt="" style={{
                    width: isNarrow || headerCollapsed ? 80 : 120,
                    height: isNarrow || headerCollapsed ? 80 : 120,
                    borderRadius: isNarrow || headerCollapsed ? Radius.md : Radius.lg,
                    ...(!isNarrow ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }} />
                ) : (
                  <div style={{
                    width: isNarrow || headerCollapsed ? 80 : 120,
                    height: isNarrow || headerCollapsed ? 80 : 120,
                    borderRadius: isNarrow || headerCollapsed ? Radius.md : Radius.lg,
                    ...(!isNarrow ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }} />
                )}
                <div>
                  <h1 style={{
                    marginBottom: isNarrow || headerCollapsed ? Gap.xs : Gap.sm,
                    ...(!isNarrow ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }}>{song?.title ?? songId}</h1>
                  <p style={{
                    fontSize: isNarrow || headerCollapsed ? Font.md : Font.lg,
                    ...(!isNarrow ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }}>{song?.artist ?? t('common.unknownArtist')}{song?.year ? ` · ${song.year}` : ''}</p>
                </div>
              </div>
              <div className={s.headerRight}>
                <div style={{
                  width: 56,
                  height: 56,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  transform: isNarrow || headerCollapsed ? 'scale(0.857)' : 'scale(1)',
                  ...(!isNarrow ? { transition: 'transform 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                }}>
                  <InstrumentIcon instrument={instKey} size={56} />
                </div>
                <span className={s.instLabel}>{instLabel}</span>
              </div>
            </div>
          </div>
        </div>

      <div ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
        <div className={s.container}>

        {error && <div className={s.centerError}>{error}</div>}

        {!error && (
          <>
            {loadPhase !== 'contentIn' && (
              <div
                className={s.spinnerContainer} style={{
                  ...(loadPhase === 'spinnerOut'
                    ? { animation: 'fadeOut 150ms ease-out forwards' }
                    : {}),
                }}
              >
                <div className={s.arcSpinner} />
              </div>
            )}
            {loadPhase === 'contentIn' && (
            <div ref={listRef} className={s.list}>
              {entries.map((e, i) => {
                const isPlayer = playerData?.accountId === e.accountId;
                // Rows stagger on first load and pagination, skip on cache
                const delay = animMode === 'cached' ? null : staggerDelay(i, STAGGER_INTERVAL, maxVisibleRows);
                const staggerStyle: React.CSSProperties | undefined = delay != null
                  ? { opacity: 0, animation: `fadeInUp 300ms ease-out ${delay}ms forwards` }
                  : undefined;
                const rowClass = isPlayer ? s.rowHighlight : s.row;
                const rowStyle = isMobile ? { ...baseStyle, gap: Gap.md, padding: `0 ${Gap.md}px`, height: 40 } : baseStyle;
                return (
                <Link
                  key={e.accountId}
                  ref={isPlayer ? playerRowRef : undefined}
                  to={isPlayer ? '/statistics' : `/player/${e.accountId}`}
                  state={{ backTo: location.pathname }}
                  style={{ ...rowStyle, ...staggerStyle }}
                  onAnimationEnd={(ev) => {
                    const el = ev.currentTarget;
                    el.style.opacity = '';
                    el.style.animation = '';
                  }}
                >
                  <span style={{ ...s.colRank, ...(isPlayer ? { fontWeight: 700 } : {}) }}>#{(e.rank ?? startRank + i + 1).toLocaleString()}</span>
                  <span style={{ ...s.colName, ...(isPlayer ? { fontWeight: 700 } : {}) }}>
                    {e.displayName || t('common.unknownUser')}
                  </span>
                  <span className={s.seasonScoreGroup}>
                    {showSeason && e.season != null && (
                      <SeasonPill season={e.season} />
                    )}
                    <span className={s.colScore} style={{ width: scoreWidth }}>
                      {e.score.toLocaleString()}
                    </span>
                  </span>
                  {showAccuracy && (
                  <span className={s.colAcc}>
                    {e.accuracy != null
                      ? (() => {
                          const pct = e.accuracy / 10000;
                          const r1 = pct.toFixed(1);
                          const text = r1.endsWith('.0') ? `${Math.round(pct)}%` : `${r1}%`;
                          return e.isFullCombo
                            ? <span className={s.fcAccBadge}>{text}</span>
                            : <span style={{ color: accuracyColor(pct) }}>{text}</span>;
                        })()
                      : '—'}
                  </span>
                  )}
                  {showStars && (
                  <span className={s.colStars}>
                    {e.stars != null && e.stars > 0
                      ? (() => {
                          const allGold = e.stars >= 6;
                          const count = allGold ? 5 : e.stars;
                          const src = allGold ? `${import.meta.env.BASE_URL}star_gold.png` : `${import.meta.env.BASE_URL}star_white.png`;
                          return Array.from({ length: count }, (_, i) => (
                            <img key={i} src={src} alt="★" className={s.starImg} />
                          ));
                        })()
                      : '—'}
                  </span>
                  )}
                </Link>
                );
              })}
              {entries.length === 0 && (
                <div className={s.emptyRow}>{t('leaderboard.noEntriesOnPage')}</div>
              )}
            </div>
            )}
          </>
        )}
      </div>
      </div>

        {hasLoadedOnce.current && !error && totalPages > 1 && (() => {
          return (
        <div
          style={{ ...s.pagination, ...(isMobile ? { justifyContent: 'space-between', gap: 0 } : {}), ...(hasFab ? { paddingBottom: 96 } : {}) }}
        >
          <button
            style={{
              ...s.pageButton,
              ...(page === 0 ? s.pageButtonDisabled : {}),
            }}
            disabled={page === 0}
            onClick={() => void fetchPage(0)}
          >
            « First
          </button>
          <button
            style={{
              ...s.pageButton,
              ...(page === 0 ? s.pageButtonDisabled : {}),
            }}
            disabled={page === 0}
            onClick={() => void fetchPage(page - 1)}
          >
            ‹ Prev
          </button>
          <span className={s.pageInfo}>
            <span className={s.pageInfoBadge}>{page + 1} / {totalPages}</span>
          </span>
          <button
            style={{
              ...s.pageButton,
              ...(page >= totalPages - 1
                ? s.pageButtonDisabled
                : {}),
            }}
            disabled={page >= totalPages - 1}
            onClick={() => void fetchPage(page + 1)}
          >
            Next ›
          </button>
          <button
            style={{
              ...s.pageButton,
              ...(page >= totalPages - 1
                ? s.pageButtonDisabled
                : {}),
            }}
            disabled={page >= totalPages - 1}
            onClick={() => void fetchPage(totalPages - 1)}
          >
            Last »
          </button>
        </div>
          );
        })()}

      {playerScore && playerData && songId && isScoreValid(songId, instKey, playerScore.score) && (() => {
        return (
        <div
          style={{ ...s.playerFooter, ...(hasFab ? s.playerFooterFab : {}), ...(hasFab && IS_PWA ? { bottom: 84 + Gap.section - Gap.md } : {}) }}
          onClick={() => navigate('/statistics')} role="button" tabIndex={0}
        >
          <div className={hasFab ? 'fab-player-footer' : undefined} style={{ ...s.playerFooterRow, cursor: 'pointer', ...(isMobile ? { gap: Gap.md, paddingLeft: Gap.md, paddingRight: Gap.md } : {}) }}>
            <span style={{ ...s.colRank, fontWeight: 700 }}>#{playerScore.rank.toLocaleString()}</span>
            <span style={{ ...s.colName, fontWeight: 700 }}>{playerData.displayName}</span>
            <span className={s.seasonScoreGroup}>
              {showSeason && playerScore.season != null && (
                <SeasonPill season={playerScore.season} />
              )}
              <span className={s.colScore} style={{ width: scoreWidth }}>
                {playerScore.score.toLocaleString()}
              </span>
            </span>
            {showAccuracy && (
            <span className={s.colAcc}>
              {playerScore.accuracy != null
                ? (() => {
                    const pct = playerScore.accuracy / 10000;
                    const r1 = pct.toFixed(1);
                    const text = r1.endsWith('.0') ? `${Math.round(pct)}%` : `${r1}%`;
                    return playerScore.isFullCombo
                      ? <span className={s.fcAccBadge}>{text}</span>
                      : text;
                  })()
                : '\u2014'}
            </span>
            )}
            {showStars && (
            <span className={s.colStars}>
              {playerScore.stars != null && playerScore.stars > 0
                ? (() => {
                    const allGold = playerScore.stars >= 6;
                    const count = allGold ? 5 : playerScore.stars;
                    const src = allGold ? `${import.meta.env.BASE_URL}star_gold.png` : `${import.meta.env.BASE_URL}star_white.png`;
                    return Array.from({ length: count }, (_, i) => (
                      <img key={i} src={src} alt="\u2605" className={s.starImg} />
                    ));
                  })()
                : '\u2014'}
            </span>
            )}
          </div>
        </div>
        );
      })()}
    </div>
  );
}

