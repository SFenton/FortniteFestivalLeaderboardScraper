import { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { useParams, Link, useSearchParams } from 'react-router-dom';
import { useFestival } from '../contexts/FestivalContext';
import { usePlayerData } from '../contexts/PlayerDataContext';
import { useIsMobile } from '../hooks/useIsMobile';
import { api } from '../api/client';
import {
  INSTRUMENT_LABELS,
  type InstrumentKey,
  type LeaderboardEntry,
} from '../models';
import { InstrumentIcon } from '../components/InstrumentIcons';
import { Colors, Font, Gap, Radius, Layout, MaxWidth, Size } from '../theme';

const PAGE_SIZE = 25;

export default function LeaderboardPage() {
  const { songId, instrument } = useParams<{
    songId: string;
    instrument: string;
  }>();
  const {
    state: { songs },
  } = useFestival();

  const song = songs.find((s) => s.songId === songId);
  const instKey = instrument as InstrumentKey;
  const instLabel = INSTRUMENT_LABELS[instKey] ?? instrument;

  const isMobile = useIsMobile();

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
  const [loadPhase, setLoadPhase] = useState<'loading' | 'spinnerOut' | 'contentIn'>('loading');

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
  useEffect(() => {
    if (loading || error) {
      setLoadPhase('loading');
      return;
    }
    setLoadPhase('spinnerOut');
    const id = setTimeout(() => setLoadPhase('contentIn'), 500);
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

        <div style={styles.headerBar}>
          <div style={styles.container}>
            <div style={styles.headerContent}>
              <div style={styles.headerLeft}>
                {song?.albumArt ? (
                  <img src={song.albumArt} alt="" style={styles.headerArt} />
                ) : (
                  <div style={{ ...styles.headerArt, ...styles.artPlaceholder }} />
                )}
                <div>
                  <h1 style={styles.songTitle}>{song?.title ?? songId}</h1>
                  <p style={styles.songArtist}>{song?.artist ?? 'Unknown Artist'}</p>
                </div>
              </div>
              <div style={styles.headerRight}>
                <InstrumentIcon instrument={instKey} size={48} />
                <span style={styles.instLabel}>{instLabel}</span>
              </div>
            </div>
          </div>
        </div>

      <div style={styles.scrollArea}>
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
                const staggerStyle: React.CSSProperties = {
                  opacity: 0,
                  animation: `fadeInUp 400ms ease-out ${(i + 1) * 125}ms forwards`,
                };
                const baseStyle = isPlayer ? { ...styles.row, ...styles.rowHighlight } : styles.row;
                const rowStyle = isMobile ? { ...baseStyle, gap: Gap.md, padding: `0 ${Gap.md}px` } : baseStyle;
                return (
                <Link
                  key={e.accountId}
                  ref={isPlayer ? playerRowRef : undefined}
                  to={`/player/${e.accountId}`}
                  style={{ ...rowStyle, ...staggerStyle }}
                  onAnimationEnd={(ev) => {
                    const el = ev.currentTarget;
                    el.style.opacity = '';
                    el.style.animation = '';
                  }}
                >
                  <span style={styles.colRank}>#{startRank + i + 1}</span>
                  <span style={styles.colName}>
                    {e.displayName ?? e.accountId.slice(0, 12)}
                  </span>
                  <span style={styles.colScore}>
                    {e.score.toLocaleString()}
                  </span>
                  <span style={styles.colAcc}>
                    {e.accuracy != null
                      ? (() => {
                          const pct = e.accuracy / 10000;
                          const r1 = pct.toFixed(1);
                          const text = r1.endsWith('.0') ? `${Math.round(pct)}%` : `${r1}%`;
                          return e.isFullCombo
                            ? <span style={styles.fcAccBadge}>{text}</span>
                            : text;
                        })()
                      : '—'}
                  </span>
                  {!isMobile && (
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

        {loadPhase === 'contentIn' && !error && totalPages > 1 && (
        <div style={styles.pagination}>
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
        <div style={styles.playerFooter} onClick={goToPlayerPage} role="button" tabIndex={0}>
          <div style={{ ...styles.playerFooterRow, cursor: 'pointer', ...(isMobile ? { gap: Gap.md, padding: `0 ${Gap.md}px` } : {}) }}>
            <span style={styles.colRank}>#{playerScore.rank.toLocaleString()}</span>
            <span style={styles.colName}>{playerData.displayName}</span>
            <span style={styles.colScore}>{playerScore.score.toLocaleString()}</span>
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
            {!isMobile && (
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
    paddingTop: Gap.md,
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
    height: 64,
    borderRadius: Radius.md,
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
    textDecoration: 'none',
    color: 'inherit',
    transition: 'background-color 0.15s',
    fontSize: Font.lg,
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
    width: 64,
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
    width: 110,
    flexShrink: 0,
    textAlign: 'right' as const,
    fontWeight: 600,
    color: Colors.textPrimary,
    fontVariantNumeric: 'tabular-nums',
  },
  colAcc: {
    width: 70,
    flexShrink: 0,
    textAlign: 'center' as const,
    fontWeight: 600,
    color: Colors.accentBlueBright,
    fontVariantNumeric: 'tabular-nums',
    marginLeft: Gap.md,
  },
  colAccFC: {
    color: Colors.gold,
  },
  fcAccBadge: {
    color: Colors.gold,
    backgroundColor: 'transparent',
    padding: `${Gap.xs}px ${Gap.sm}px`,
    borderRadius: Radius.xs,
    border: `2px solid ${Colors.goldStroke}`,
    fontWeight: 700,
    fontStyle: 'italic' as const,
    display: 'inline-block',
    transform: 'skewX(-8deg)',
  },
  colStars: {
    width: 110,
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 3,
  },
  starImg: {
    width: 20,
    height: 20,
    objectFit: 'contain' as const,
  },
  fcBadge: {
    fontSize: Font.sm,
    fontWeight: 700,
    color: Colors.gold,
    backgroundColor: Colors.goldBg,
    padding: `${Gap.xs}px ${Gap.sm}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.goldStroke}`,
  },
  pagination: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.md,
    flexShrink: 0,
    padding: `${Gap.md}px 0`,
  },
  pageButton: {
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderRadius: Radius.sm,
    border: `1px solid ${Colors.glassBorder}`,
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
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
    minWidth: '7.5em',
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
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
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
    height: 64,
    padding: `0 ${Gap.xl}px`,
    borderRadius: Radius.md,
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
    fontSize: Font.lg,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
  },
};
