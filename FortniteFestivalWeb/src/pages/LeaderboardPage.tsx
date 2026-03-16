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
import { Colors, Font, Gap, Radius, Layout, MaxWidth, Size, goldFill, goldOutlineSkew, frostedCard } from '../theme';
import { staggerDelay } from '../utils/stagger';
import { useScrollMask } from '../hooks/useScrollMask';
import { useStaggerRush } from '../hooks/useStaggerRush';
import { useIsMobile, useIsMobileChrome } from '../hooks/useIsMobile';
import { useScoreFilter } from '../hooks/useScoreFilter';
import { useMediaQuery } from '../hooks/useMediaQuery';
import { IS_PWA } from '../utils/isPwa';

const PAGE_SIZE = 25;

function accuracyColor(pct: number): string {
  const t = Math.min(Math.max(pct / 100, 0), 1);
  const r = Math.round(220 * (1 - t) + 46 * t);
  const g = Math.round(40 * (1 - t) + 204 * t);
  const b = Math.round(40 * (1 - t) + 113 * t);
  return `rgb(${r},${g},${b})`;
}

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
    return <div style={styles.center}>{t('leaderboard.notFound')}</div>;
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
          paddingTop: isNarrow || headerCollapsed ? Gap.md : Layout.paddingTop,
          paddingBottom: Gap.section,
          ...(!isNarrow ? { transition: 'padding 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
        }}>
          <div style={styles.container}>
            <div style={{
              ...styles.headerContent,
              ...(!isNarrow ? { transition: 'margin 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
            }}>
              <div style={styles.headerLeft}>
                {song?.albumArt ? (
                  <img src={song.albumArt} alt="" style={{
                    ...styles.headerArt,
                    width: isNarrow || headerCollapsed ? 80 : 120,
                    height: isNarrow || headerCollapsed ? 80 : 120,
                    borderRadius: isNarrow || headerCollapsed ? Radius.md : Radius.lg,
                    ...(!isNarrow ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }} />
                ) : (
                  <div style={{
                    ...styles.headerArt,
                    ...styles.artPlaceholder,
                    width: isNarrow || headerCollapsed ? 80 : 120,
                    height: isNarrow || headerCollapsed ? 80 : 120,
                    borderRadius: isNarrow || headerCollapsed ? Radius.md : Radius.lg,
                    ...(!isNarrow ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }} />
                )}
                <div>
                  <h1 style={{
                    ...styles.songTitle,
                    marginBottom: isNarrow || headerCollapsed ? Gap.xs : Gap.sm,
                    ...(!isNarrow ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }}>{song?.title ?? songId}</h1>
                  <p style={{
                    ...styles.songArtist,
                    fontSize: isNarrow || headerCollapsed ? Font.md : Font.lg,
                    ...(!isNarrow ? { transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)' } : {}),
                  }}>{song?.artist ?? t('common.unknownArtist')}{song?.year ? ` · ${song.year}` : ''}</p>
                </div>
              </div>
              <div style={styles.headerRight}>
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
                <span style={styles.instLabel}>{instLabel}</span>
              </div>
            </div>
          </div>
        </div>

      <div ref={scrollRef} onScroll={handleScroll} style={styles.scrollArea}>
        <div style={styles.container}>

        {error && <div style={styles.centerError}>{error}</div>}

        {!error && (
          <>
            {loadPhase !== 'contentIn' && (
              <div
                style={{
                  ...styles.spinnerContainer,
                  ...(loadPhase === 'spinnerOut'
                    ? { animation: 'fadeOut 150ms ease-out forwards' }
                    : {}),
                }}
              >
                <div style={styles.arcSpinner} />
              </div>
            )}
            {loadPhase === 'contentIn' && (
            <div ref={listRef} style={styles.list}>
              {entries.map((e, i) => {
                const isPlayer = playerData?.accountId === e.accountId;
                // Rows stagger on first load and pagination, skip on cache
                const delay = animMode === 'cached' ? null : staggerDelay(i, STAGGER_INTERVAL, maxVisibleRows);
                const staggerStyle: React.CSSProperties | undefined = delay != null
                  ? { opacity: 0, animation: `fadeInUp 300ms ease-out ${delay}ms forwards` }
                  : undefined;
                const baseStyle = isPlayer ? { ...styles.row, ...styles.rowHighlight } : styles.row;
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
                  <span style={{ ...styles.colRank, ...(isPlayer ? { fontWeight: 700 } : {}) }}>#{(e.rank ?? startRank + i + 1).toLocaleString()}</span>
                  <span style={{ ...styles.colName, ...(isPlayer ? { fontWeight: 700 } : {}) }}>
                    {e.displayName || t('common.unknownUser')}
                  </span>
                  <span style={styles.seasonScoreGroup}>
                    {showSeason && e.season != null && (
                      <SeasonPill season={e.season} />
                    )}
                    <span style={{ ...styles.colScore, width: scoreWidth }}>
                      {e.score.toLocaleString()}
                    </span>
                  </span>
                  {showAccuracy && (
                  <span style={styles.colAcc}>
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
                  {showStars && (
                  <span style={styles.colStars}>
                    {e.stars != null && e.stars > 0
                      ? (() => {
                          const allGold = e.stars >= 6;
                          const count = allGold ? 5 : e.stars;
                          const src = allGold ? `${import.meta.env.BASE_URL}star_gold.png` : `${import.meta.env.BASE_URL}star_white.png`;
                          return Array.from({ length: count }, (_, i) => (
                            <img key={i} src={src} alt="★" style={styles.starImg} />
                          ));
                        })()
                      : '—'}
                  </span>
                  )}
                </Link>
                );
              })}
              {entries.length === 0 && (
                <div style={styles.emptyRow}>{t('leaderboard.noEntriesOnPage')}</div>
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
          style={{ ...styles.pagination, ...(isMobile ? { justifyContent: 'space-between', gap: 0 } : {}), ...(hasFab ? { paddingBottom: 96 } : {}) }}
        >
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
            <span style={styles.pageInfoBadge}>{page + 1} / {totalPages}</span>
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
          );
        })()}

      {playerScore && playerData && songId && isScoreValid(songId, instKey, playerScore.score) && (() => {
        return (
        <div
          style={{ ...styles.playerFooter, ...(hasFab ? styles.playerFooterFab : {}), ...(hasFab && IS_PWA ? { bottom: 84 + Gap.section - Gap.md } : {}) }}
          onClick={() => navigate('/statistics')} role="button" tabIndex={0}
        >
          <div className={hasFab ? 'fab-player-footer' : undefined} style={{ ...styles.playerFooterRow, cursor: 'pointer', ...(isMobile ? { gap: Gap.md, paddingLeft: Gap.md, paddingRight: Gap.md } : {}) }}>
            <span style={{ ...styles.colRank, fontWeight: 700 }}>#{playerScore.rank.toLocaleString()}</span>
            <span style={{ ...styles.colName, fontWeight: 700 }}>{playerData.displayName}</span>
            <span style={styles.seasonScoreGroup}>
              {showSeason && playerScore.season != null && (
                <SeasonPill season={playerScore.season} />
              )}
              <span style={{ ...styles.colScore, width: scoreWidth }}>
                {playerScore.score.toLocaleString()}
              </span>
            </span>
            {showAccuracy && (
            <span style={styles.colAcc}>
              {playerScore.accuracy != null
                ? (() => {
                    const pct = playerScore.accuracy / 10000;
                    const r1 = pct.toFixed(1);
                    const text = r1.endsWith('.0') ? `${Math.round(pct)}%` : `${r1}%`;
                    return playerScore.isFullCombo
                      ? <span style={styles.fcAccBadge}>{text}</span>
                      : text;
                  })()
                : '\u2014'}
            </span>
            )}
            {showStars && (
            <span style={styles.colStars}>
              {playerScore.stars != null && playerScore.stars > 0
                ? (() => {
                    const allGold = playerScore.stars >= 6;
                    const count = allGold ? 5 : playerScore.stars;
                    const src = allGold ? `${import.meta.env.BASE_URL}star_gold.png` : `${import.meta.env.BASE_URL}star_white.png`;
                    return Array.from({ length: count }, (_, i) => (
                      <img key={i} src={src} alt="\u2605" style={styles.starImg} />
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
  backLink: {
    color: Colors.accentBlue,
    textDecoration: 'none',
    fontSize: Font.md,
    display: 'inline-block',
    marginBottom: Gap.md,
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
  meta: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
  },
  metaPage: {
    color: Colors.textMuted,
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
    textDecoration: 'none',
    color: 'inherit',
    transition: 'background-color 0.15s',
    fontSize: Font.md,
  },
  emptyRow: {
    padding: `${Gap.xl}px`,
    textAlign: 'center' as const,
    color: Colors.textMuted,
  },
  rowHighlight: {
    backgroundColor: 'rgba(75, 15, 99, 0.75)',
    border: `1px solid rgba(124, 58, 237, 0.5)`,
  },
  colRank: {
    width: 48,
    flexShrink: 0,
    color: Colors.textPrimary,
    fontSize: Font.md,
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
    textAlign: 'center' as const,
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
    marginLeft: 0,
  },
  colAccFC: {
    color: Colors.gold,
  },
  fcAccBadge: {
    ...goldOutlineSkew,
    fontSize: Font.lg,
    textAlign: 'center' as const,
  },
  colStars: {
    width: 110,
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: 3,
  },
  starImg: {
    width: 20,
    height: 20,
    objectFit: 'contain' as const,
  },
  fcBadge: {
    ...goldFill,
    fontSize: Font.sm,
    fontWeight: 700,
    padding: `${Gap.xs}px ${Gap.sm}px`,
    borderRadius: Radius.xs,
  },
  pagination: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.md,
    flexShrink: 0,
    paddingTop: Gap.md,
    paddingBottom: Gap.md,
    paddingLeft: Layout.paddingHorizontal,
    paddingRight: Layout.paddingHorizontal,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    width: '100%',
    boxSizing: 'border-box' as const,
    position: 'relative' as const,
    zIndex: 1,
  },
  pageButton: {
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderRadius: Radius.sm,
    ...frostedCard,
    backgroundColor: Colors.backgroundCard,
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
    textAlign: 'center' as const,
  },
  pageInfoBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: Font.sm,
    color: Colors.textSecondary,
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderRadius: Radius.sm,
    ...frostedCard,
    backgroundColor: Colors.backgroundCard,
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
  playerFooter: {
    flexShrink: 0,
    zIndex: 20,
    paddingTop: Gap.md,
    paddingBottom: Gap.md,
    paddingLeft: Layout.paddingHorizontal,
    paddingRight: Layout.paddingHorizontal,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    boxSizing: 'border-box' as const,
    width: '100%',
  },
  playerFooterFab: {
    position: 'fixed' as const,
    bottom: 84,
    left: 0,
    right: 0,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    paddingTop: 0,
    paddingBottom: 0,
    paddingLeft: Layout.paddingHorizontal,
    paddingRight: Layout.paddingHorizontal,
    boxSizing: 'border-box' as const,
    zIndex: 150,
    pointerEvents: 'auto' as const,
  },
  playerFooterRow: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    height: 48,
    padding: `0 ${Gap.xl}px`,
    borderRadius: Radius.md,
    ...frostedCard,
    backgroundColor: 'rgba(75, 15, 99, 0.75)',
    border: `1px solid rgba(124, 58, 237, 0.5)`,
    fontSize: Font.md,
  },
};
