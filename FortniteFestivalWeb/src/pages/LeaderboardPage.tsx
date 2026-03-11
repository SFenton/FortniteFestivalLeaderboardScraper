import { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { useParams, Link, useSearchParams, useLocation } from 'react-router-dom';
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
import { staggerDelay, estimateVisibleCount } from '../utils/stagger';
import { useScrollMask } from '../hooks/useScrollMask';
import { useIsMobile } from '../hooks/useIsMobile';

const PAGE_SIZE = 25;

function accuracyColor(pct: number): string {
  const t = Math.min(Math.max(pct / 100, 0), 1);
  const r = Math.round(220 * (1 - t) + 46 * t);
  const g = Math.round(40 * (1 - t) + 204 * t);
  const b = Math.round(40 * (1 - t) + 113 * t);
  return `rgb(${r},${g},${b})`;
}

export default function LeaderboardPage() {
  const { songId, instrument } = useParams<{
    songId: string;
    instrument: string;
  }>();
  const location = useLocation();
  const {
    state: { songs },
  } = useFestival();

  const song = songs.find((s) => s.songId === songId);
  const instKey = instrument as InstrumentKey;
  const instLabel = INSTRUMENT_LABELS[instKey] ?? instrument;

  const [windowWidth, setWindowWidth] = useState(window.innerWidth);
  useEffect(() => {
    const onResize = () => setWindowWidth(window.innerWidth);
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);
  const showAccuracy = windowWidth >= 420;
  const showSeason = windowWidth >= 520;
  const showStars = windowWidth >= 768;
  const isMobile = windowWidth < 420;
  const hasFab = useIsMobile();

  const [searchParams, setSearchParams] = useSearchParams();

  const { playerData } = usePlayerData();
  const playerScore = useMemo(() => {
    if (!playerData || !songId) return null;
    return playerData.scores.find(
      (s) => s.songId === songId && s.instrument === instKey,
    ) ?? null;
  }, [playerData, songId, instKey]);

  const [entries, setEntries] = useState<LeaderboardEntry[]>([]);
  const [totalEntries, setTotalEntries] = useState(0);
  const [localEntries, setLocalEntries] = useState(0);
  const [page, setPage] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const playerRowRef = useRef<HTMLAnchorElement | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const [headerCollapsed, setHeaderCollapsed] = useState(false);
  const headerPinned = useRef(false);
  const [loadPhase, setLoadPhase] = useState<'loading' | 'spinnerOut' | 'contentIn'>('loading');
  const updateScrollMask = useScrollMask(scrollRef, [loadPhase, entries.length]);
  const handleScroll = useCallback(() => {
    updateScrollMask();
    const el = scrollRef.current;
    if (!el) return;
    // If pinned (after pagination), only unpin once user scrolls past threshold
    if (headerPinned.current) {
      if (el.scrollTop > 40) headerPinned.current = false;
      return;
    }
    setHeaderCollapsed(el.scrollTop > 40);
  }, [updateScrollMask]);

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
    const pageParam = parseInt(searchParams.get('page') ?? '', 10);
    const startPage = !isNaN(pageParam) && pageParam >= 1 ? pageParam - 1 : 0;
    void fetchPage(startPage);
  }, [fetchPage]);

  // Spinner → staggered-content transition
  const hasLoadedOnce = useRef(false);
  useEffect(() => {
    if (loading || error) {
      setLoadPhase('loading');
      // Pin header state and scroll to top so new content staggers from top
      headerPinned.current = true;
      scrollRef.current?.scrollTo(0, 0);
      return;
    }
    setLoadPhase('spinnerOut');
    const id = setTimeout(() => {
      setLoadPhase('contentIn');
      // On initial load, let header be expanded and unpin immediately.
      // On pagination, keep pinned — scroll handler will unpin once past threshold.
      if (!hasLoadedOnce.current) {
        hasLoadedOnce.current = true;
        headerPinned.current = false;
        setHeaderCollapsed(false);
      }
    }, 500);
    return () => clearTimeout(id);
  }, [loading, error]);

  useEffect(() => {
    if (loadPhase !== 'contentIn' || !searchParams.get('navToPlayer')) return;
    const playerIndex = playerData ? entries.findIndex(e => e.accountId === playerData.accountId) : -1;
    if (playerIndex < 0) {
      searchParams.delete('navToPlayer');
      setSearchParams(searchParams, { replace: true });
      return;
    }
    // Wait for the player's row stagger animation to finish: (index+1)*125ms delay + 400ms duration
    const scrollDelay = (playerIndex + 1) * 125 + 400;
    const id = setTimeout(() => {
      playerRowRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      searchParams.delete('navToPlayer');
      setSearchParams(searchParams, { replace: true });
    }, scrollDelay);
    return () => clearTimeout(id);
  }, [loadPhase, entries, playerData, searchParams, setSearchParams]);

  if (!songId || !instrument) {
    return <div style={styles.center}>Not found</div>;
  }

  const goToPlayerPage = useCallback(() => {
    if (!playerScore) return;
    const playerPage = Math.floor((playerScore.rank - 1) / PAGE_SIZE);
    setSearchParams({ page: String(playerPage + 1), navToPlayer: 'true' }, { replace: true });
    void fetchPage(playerPage);
  }, [playerScore, fetchPage, setSearchParams]);

  const startRank = page * PAGE_SIZE;

  const scoreWidth = useMemo(() => {
    const maxLen = Math.max(
      ...entries.map((e) => e.score.toLocaleString().length),
      1,
    );
    return `${maxLen}ch`;
  }, [entries]);

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
          paddingTop: headerCollapsed ? Gap.md : Layout.paddingTop,
          transition: 'padding 300ms cubic-bezier(0.4, 0, 0.2, 1)',
        }}>
          <div style={styles.container}>
            <div style={styles.headerContent}>
              <div style={styles.headerLeft}>
                {song?.albumArt ? (
                  <img src={song.albumArt} alt="" style={{
                    ...styles.headerArt,
                    width: headerCollapsed ? 80 : 120,
                    height: headerCollapsed ? 80 : 120,
                    borderRadius: headerCollapsed ? Radius.md : Radius.lg,
                    transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)',
                  }} />
                ) : (
                  <div style={{
                    ...styles.headerArt,
                    ...styles.artPlaceholder,
                    width: headerCollapsed ? 80 : 120,
                    height: headerCollapsed ? 80 : 120,
                    borderRadius: headerCollapsed ? Radius.md : Radius.lg,
                    transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)',
                  }} />
                )}
                <div>
                  <h1 style={{
                    ...styles.songTitle,
                    marginBottom: headerCollapsed ? Gap.xs : Gap.sm,
                    transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)',
                  }}>{song?.title ?? songId}</h1>
                  <p style={{
                    ...styles.songArtist,
                    fontSize: headerCollapsed ? Font.md : Font.lg,
                    transition: 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)',
                  }}>{song?.artist ?? 'Unknown Artist'}</p>
                </div>
              </div>
              <div style={styles.headerRight}>
                <div style={{
                  width: 56,
                  height: 56,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  transform: headerCollapsed ? 'scale(0.857)' : 'scale(1)',
                  transition: 'transform 300ms cubic-bezier(0.4, 0, 0.2, 1)',
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
                    ? { animation: 'fadeOut 500ms ease-out forwards' }
                    : {}),
                }}
              >
                <div style={styles.arcSpinner} />
              </div>
            )}
            {loadPhase === 'contentIn' && (
            <div style={styles.list}>
              {entries.map((e, i) => {
                const isPlayer = playerData?.accountId === e.accountId;
                const delay = staggerDelay(i, 125, estimateVisibleCount(56));
                const staggerStyle: React.CSSProperties | undefined = delay != null
                  ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${delay}ms forwards` }
                  : undefined;
                const baseStyle = isPlayer ? { ...styles.row, ...styles.rowHighlight } : styles.row;
                const rowStyle = isMobile ? { ...baseStyle, gap: Gap.md, padding: `0 ${Gap.md}px`, height: 40 } : baseStyle;
                return (
                <Link
                  key={e.accountId}
                  ref={isPlayer ? playerRowRef : undefined}
                  to={`/player/${e.accountId}`}
                  state={{ backTo: location.pathname }}
                  style={{ ...rowStyle, ...staggerStyle }}
                  onAnimationEnd={(ev) => {
                    const el = ev.currentTarget;
                    el.style.opacity = '';
                    el.style.animation = '';
                  }}
                >
                  <span style={styles.colRank}>#{(e.rank ?? startRank + i + 1).toLocaleString()}</span>
                  <span style={styles.colName}>
                    {e.displayName ?? e.accountId.slice(0, 12)}
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
                          const src = allGold ? '/app/star_gold.png' : '/app/star_white.png';
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
                <div style={styles.emptyRow}>No entries on this page</div>
              )}
            </div>
            )}
          </>
        )}
      </div>
      </div>

        {hasLoadedOnce.current && !error && totalPages > 1 && (
        <div style={{ ...styles.pagination, ...(isMobile ? { justifyContent: 'space-between', gap: 0 } : {}) }}>
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
      )}

      {playerScore && playerData && (
        <div style={{ ...styles.playerFooter, ...(hasFab ? { paddingBottom: 94 } : {}) }} onClick={goToPlayerPage} role="button" tabIndex={0}>
          <div style={{ ...styles.playerFooterRow, cursor: 'pointer', ...(isMobile ? { gap: Gap.md, padding: `0 ${Gap.md}px` } : {}) }}>
            <span style={styles.colRank}>#{playerScore.rank.toLocaleString()}</span>
            <span style={styles.colName}>{playerData.displayName}</span>
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
                    const src = allGold ? '/app/star_gold.png' : '/app/star_white.png';
                    return Array.from({ length: count }, (_, i) => (
                      <img key={i} src={src} alt="\u2605" style={styles.starImg} />
                    ));
                  })()
                : '\u2014'}
            </span>
            )}
          </div>
        </div>
      )}
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
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px`,
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
    paddingBottom: Gap.md,
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
    backgroundColor: 'rgba(88, 166, 255, 0.18)',
    border: `1px solid rgba(88, 166, 255, 0.45)`,
  },
  colRank: {
    width: 48,
    flexShrink: 0,
    color: Colors.textTertiary,
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
    width: 60,
    flexShrink: 0,
    textAlign: 'center' as const,
    fontWeight: 600,
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
    padding: `${Gap.md}px ${Layout.paddingHorizontal}px`,
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
    padding: `${Gap.md}px ${Layout.paddingHorizontal}px`,
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
    fontSize: Font.lg,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
  },
};
