import { useEffect, useState, useCallback, useRef } from 'react';
import { useParams, Link, useNavigate, useSearchParams } from 'react-router-dom';
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
import { Colors, Font, Gap, Radius, Layout, MaxWidth, goldOutlineSkew } from '../theme';
import SeasonPill from '../components/SeasonPill';
import ScoreHistoryChart from '../components/ScoreHistoryChart';
import { InstrumentIcon } from '../components/InstrumentIcons';
import { useSettings, visibleInstruments } from '../contexts/SettingsContext';

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

export default function SongDetailPage() {
  const { songId } = useParams<{ songId: string }>();
  const [searchParams] = useSearchParams();
  const defaultInstrument = (searchParams.get('instrument') as InstrumentKey) || undefined;
  const [windowWidth, setWindowWidth] = useState(window.innerWidth);
  useEffect(() => {
    const onResize = () => setWindowWidth(window.innerWidth);
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);
  const {
    state: { songs },
  } = useFestival();
  const { player } = useTrackedPlayer();
  const { settings } = useSettings();
  const activeInstruments = visibleInstruments(settings);

  const [playerScores, setPlayerScores] = useState<PlayerScore[]>([]);
  const [playerScoresReady, setPlayerScoresReady] = useState(false);
  const [scoreHistory, setScoreHistory] = useState<ScoreHistoryEntry[]>([]);
  const [scoreHistoryReady, setScoreHistoryReady] = useState(false);
  const [instrumentData, setInstrumentData] = useState<Record<InstrumentKey, InstrumentData>>(
    () => Object.fromEntries(
      INSTRUMENT_KEYS.map((k) => [k, { entries: [], loading: true, error: null }]),
    ) as Record<InstrumentKey, InstrumentData>,
  );

  const song = songs.find((s) => s.songId === songId);

  // Fetch player scores
  useEffect(() => {
    if (!player || !songId) {
      setPlayerScores([]);
      setPlayerScoresReady(true);
      return;
    }
    setPlayerScoresReady(false);
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
    if (!player || !songId) {
      setScoreHistory([]);
      setScoreHistoryReady(true);
      return;
    }
    setScoreHistoryReady(false);
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

  // Fetch all instrument leaderboards in parallel
  useEffect(() => {
    if (!songId) return;
    let cancelled = false;
    setInstrumentData(
      Object.fromEntries(
        INSTRUMENT_KEYS.map((k) => [k, { entries: [], loading: true, error: null }]),
      ) as Record<InstrumentKey, InstrumentData>,
    );
    for (const inst of activeInstruments) {
      api.getLeaderboard(songId, inst, 10).then((res) => {
        if (!cancelled) {
          setInstrumentData((prev) => ({
            ...prev,
            [inst]: { entries: res.entries, loading: false, error: null },
          }));
        }
      }).catch((e) => {
        if (!cancelled) {
          setInstrumentData((prev) => ({
            ...prev,
            [inst]: { entries: [], loading: false, error: e instanceof Error ? e.message : 'Error' },
          }));
        }
      });
    }
    return () => { cancelled = true; };
  }, [songId]);

  // No player means no player-specific data to wait for
  const playerDataReady = !player || (playerScoresReady && scoreHistoryReady);
  const instrumentsReady = activeInstruments.every((k) => !instrumentData[k].loading);
  const allReady = playerDataReady && instrumentsReady;

  // Transition: spinner fade-out → staggered content fade-in
  // phase: 'loading' | 'spinnerOut' | 'contentIn'
  const [phase, setPhase] = useState<'loading' | 'spinnerOut' | 'contentIn'>('loading');

  const hasScrolled = useRef(false);

  // Reset scroll tracking when song or instrument changes
  useEffect(() => {
    hasScrolled.current = false;
  }, [songId, defaultInstrument]);

  useEffect(() => {
    if (!allReady) return;
    setPhase('spinnerOut');
    const id = setTimeout(() => setPhase('contentIn'), 500);
    return () => clearTimeout(id);
  }, [allReady]);

  // Scroll to the instrument card when arriving with ?instrument=
  useEffect(() => {
    if (phase !== 'contentIn' || !defaultInstrument || hasScrolled.current) return;
    hasScrolled.current = true;
    // Wait for stagger animations to complete before measuring position
    const id = setTimeout(() => {
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

  const stagger = (delayMs: number): React.CSSProperties => ({
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
        <div style={styles.container}>
          <div style={stagger(0)} onAnimationEnd={clearAnim}>
            <Link to="/songs" style={styles.backLink}>
              ← Back to Songs
            </Link>
          </div>
          <div style={stagger(150)} onAnimationEnd={clearAnim}>
            <SongHeader song={song} songId={songId} />
          </div>
          {player && (
            <div style={stagger(300)} onAnimationEnd={clearAnim}>
              <ScoreHistoryChart
                songId={songId}
                accountId={player.accountId}
                playerName={player.displayName}
                defaultInstrument={defaultInstrument}
                history={scoreHistory}
                visibleInstruments={activeInstruments}
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
                      playerScore={playerScores.find((s) => s.instrument === inst)}
                      playerName={player?.displayName}
                      prefetchedEntries={instrumentData[inst].entries}
                      prefetchedError={instrumentData[inst].error}
                    />
                  </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}

function SongHeader({
  song,
  songId,
}: {
  song: Song | undefined;
  songId: string;
}) {
  return (
    <div style={styles.header}>
      {song?.albumArt ? (
        <img src={song.albumArt} alt="" style={styles.headerArt} />
      ) : (
        <div style={{ ...styles.headerArt, ...styles.artPlaceholder }} />
      )}
      <div>
        <h1 style={styles.songTitle}>{song?.title ?? songId}</h1>
        <p style={styles.songArtist}>
          {song?.artist ?? 'Unknown Artist'}
        </p>
      </div>
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
  prefetchedEntries,
  prefetchedError,
}: {
  songId: string;
  instrument: InstrumentKey;
  baseDelay: number;
  windowWidth: number;
  playerScore?: PlayerScore;
  playerName?: string;
  prefetchedEntries: LeaderboardEntry[];
  prefetchedError: string | null;
}) {
  const navigate = useNavigate();

  // 2-col grid: each card gets ~half viewport. Thresholds based on card width.
  const isTwoCol = windowWidth >= 840;
  const cardWidth = isTwoCol ? windowWidth / 2 : windowWidth;
  const showAccuracy = cardWidth >= 420;
  const showSeason = cardWidth >= 520;
  const isMobile = cardWidth < 360;

  const maxScoreLen = Math.max(
    ...prefetchedEntries.map((e) => e.score.toLocaleString().length),
    playerScore ? playerScore.score.toLocaleString().length : 0,
    1,
  );
  const scoreWidth = `${maxScoreLen}ch`;

  const anim = (delayMs: number): React.CSSProperties => ({
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
          navigate(`/songs/${songId}/${instrument}`);
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
            return (
            <Link
              key={e.accountId}
              to={`/player/${e.accountId}`}
              style={{ ...styles.entryRow, ...(isMobile ? styles.entryRowMobile : {}), ...rowStagger }}
              onClick={(ev) => ev.stopPropagation()}
              onAnimationEnd={clearAnim}
            >
              <span style={styles.entryRank}>#{i + 1}</span>
              <span style={styles.entryName}>
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
        {playerName && playerScore && (() => {
          const playerDelay = baseDelay + 80 + prefetchedEntries.length * 60;
          const playerStagger = anim(playerDelay);
          return (
          <Link
            id={`player-score-${instrument}`}
            to={(() => {
              const pageNum = Math.floor((playerScore.rank - 1) / 25) + 1;
              return `/songs/${songId}/${instrument}?page=${pageNum}&navToPlayer=true`;
            })()}
            style={{ ...styles.playerEntryRow, ...(isMobile ? styles.entryRowMobile : {}), ...playerStagger }}
            onClick={(ev) => ev.stopPropagation()}
            onAnimationEnd={clearAnim}
          >
            <span style={styles.entryRank}>#{playerScore.rank.toLocaleString()}</span>
            <span style={styles.entryName}>{playerName}</span>
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
          const viewAllDelay = baseDelay + 80 + (prefetchedEntries.length + (playerScore ? 1 : 0)) * 60;
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
    minHeight: '100vh',
    backgroundColor: Colors.backgroundApp,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
    position: 'relative' as const,
    overflow: 'hidden' as const,
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
    marginBottom: Gap.xl,
    display: 'inline-block',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.section,
    marginTop: Gap.xl,
    marginBottom: Gap.section,
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
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
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
    color: Colors.textTertiary,
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
    fontSize: Font.lg,
    textAlign: 'center' as const,
  },
  viewAllButton: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: 48,
    borderRadius: Radius.md,
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
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
    backgroundColor: 'rgba(75, 15, 99, 0.45)',
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid rgba(124, 58, 237, 0.35)`,
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
