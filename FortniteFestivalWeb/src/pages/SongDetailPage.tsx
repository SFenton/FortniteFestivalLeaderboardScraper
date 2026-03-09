import { useEffect, useState, useCallback } from 'react';
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
import { Colors, Font, Gap, Radius, Layout, MaxWidth } from '../theme';
import ScoreHistoryChart from '../components/ScoreHistoryChart';

type InstrumentData = {
  entries: LeaderboardEntry[];
  loading: boolean;
  error: string | null;
};

export default function SongDetailPage() {
  const { songId } = useParams<{ songId: string }>();
  const [searchParams] = useSearchParams();
  const defaultInstrument = (searchParams.get('instrument') as InstrumentKey) || undefined;
  const {
    state: { songs },
  } = useFestival();
  const { player } = useTrackedPlayer();

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
    for (const inst of INSTRUMENT_KEYS) {
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
  const instrumentsReady = INSTRUMENT_KEYS.every((k) => !instrumentData[k].loading);
  const allReady = playerDataReady && instrumentsReady;

  // Transition: spinner fade-out → staggered content fade-in
  // phase: 'loading' | 'spinnerOut' | 'contentIn'
  const [phase, setPhase] = useState<'loading' | 'spinnerOut' | 'contentIn'>('loading');

  useEffect(() => {
    if (!allReady) return;
    setPhase('spinnerOut');
    const id = setTimeout(() => setPhase('contentIn'), 500);
    return () => clearTimeout(id);
  }, [allReady]);

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
              />
            </div>
          )}
          <div style={{ ...styles.instrumentGrid, ...stagger(450) }} onAnimationEnd={clearAnim}>
            {INSTRUMENT_KEYS.slice(0, 3).map((inst) => (
              <InstrumentCard
                key={inst}
                songId={songId}
                instrument={inst}
                difficulty={getDifficulty(song, inst)}
                playerScore={playerScores.find((s) => s.instrument === inst)}
                playerName={player?.displayName}
                prefetchedEntries={instrumentData[inst].entries}
                prefetchedError={instrumentData[inst].error}
              />
            ))}
          </div>
          <div style={{ ...styles.instrumentGrid, ...stagger(600) }} onAnimationEnd={clearAnim}>
            {INSTRUMENT_KEYS.slice(3).map((inst) => (
              <InstrumentCard
                key={inst}
                songId={songId}
                instrument={inst}
                difficulty={getDifficulty(song, inst)}
                playerScore={playerScores.find((s) => s.instrument === inst)}
                playerName={player?.displayName}
                prefetchedEntries={instrumentData[inst].entries}
                prefetchedError={instrumentData[inst].error}
              />
            ))}
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
        {song?.tempo ? (
          <span style={styles.bpmBadge}>{song.tempo} BPM</span>
        ) : null}
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
  difficulty,
  playerScore,
  playerName,
  highlight,
  prefetchedEntries,
  prefetchedError,
}: {
  songId: string;
  instrument: InstrumentKey;
  difficulty: number | undefined;
  playerScore?: PlayerScore;
  playerName?: string;
  prefetchedEntries: LeaderboardEntry[];
  prefetchedError: string | null;
}) {
  const navigate = useNavigate();

  return (
    <div
      style={{
        ...styles.card,
        cursor: 'pointer',
      }}
      onClick={() => {
        const pageNum = playerScore?.rank ? Math.floor((playerScore.rank - 1) / 25) + 1 : undefined;
        const search = pageNum ? `?page=${pageNum}&navToPlayer=true` : '';
        navigate(`/songs/${songId}/${instrument}${search}`);
      }}
    >
      <div style={styles.cardHeader}>
        <span style={styles.cardTitle}>{INSTRUMENT_LABELS[instrument]}</span>
        {difficulty != null && (
          <DifficultyBadge difficulty={difficulty} />
        )}
      </div>
      <div style={styles.cardBody}>
        {prefetchedError && <span style={styles.cardError}>{prefetchedError}</span>}
        {!prefetchedError && prefetchedEntries.length === 0 && (
          <span style={styles.cardMuted}>No entries</span>
        )}
        {!prefetchedError &&
          prefetchedEntries.map((e, i) => (
            <div key={e.accountId} style={styles.entryRow}>
              <span style={styles.entryRank}>#{i + 1}</span>
              <Link
                to={`/player/${e.accountId}`}
                style={styles.entryName}
                onClick={(ev) => ev.stopPropagation()}
              >
                {e.displayName ?? e.accountId.slice(0, 8)}
              </Link>
              <span style={styles.entryScore}>
                {e.score.toLocaleString()}
              </span>
              {e.isFullCombo && <span style={styles.fcBadge}>FC</span>}
            </div>
          ))}
        {!prefetchedError && prefetchedEntries.length > 0 && (
          <div style={styles.viewAll}>View full leaderboard →</div>
        )}
        {playerName && (
          <div style={styles.playerScoreSection}>
            <div style={styles.playerScoreLabel}>{playerName}</div>
            {playerScore ? (
              <div style={styles.playerScoreRow}>
                <span style={styles.playerRank}>#{playerScore.rank.toLocaleString()}</span>
                <span style={styles.playerScoreValue}>
                  {playerScore.score.toLocaleString()}
                </span>
                {playerScore.isFullCombo && <span style={styles.fcBadge}>FC</span>}
                {playerScore.accuracy != null && playerScore.accuracy > 0 && (
                  <span style={styles.playerAccuracy}>
                    {(playerScore.accuracy / 10000).toFixed(2)}%
                  </span>
                )}
                {playerScore.stars != null && (
                  <span style={styles.playerStars}>
                    {'★'.repeat(playerScore.stars)}
                  </span>
                )}
              </div>
            ) : (
              <div style={styles.playerScoreRow}>
                <span style={styles.notPlayed}>Not played</span>
              </div>
            )}
          </div>
        )}
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
    gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))',
    gap: 0,
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
    borderRadius: Radius.lg,
    overflow: 'hidden',
  },
  card: {
    backgroundColor: 'transparent',
    border: `1px solid ${Colors.glassBorder}`,
    borderRadius: Radius.lg,
    overflow: 'hidden',
    display: 'flex',
    flexDirection: 'column' as const,
    height: '100%',
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: `${Gap.md}px ${Gap.xl}px`,
    backgroundColor: Colors.accentPurpleDark,
  },
  cardTitle: {
    fontSize: Font.lg,
    fontWeight: 600,
  },
  cardBody: {
    padding: Gap.xl,
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
    gap: Gap.md,
    padding: `${Gap.sm}px 0`,
    borderBottom: `1px solid ${Colors.borderSubtle}`,
  },
  entryRank: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
    width: 32,
    flexShrink: 0,
  },
  entryName: {
    fontSize: Font.md,
    flex: 1,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    textDecoration: 'none',
    color: 'inherit',
  },
  entryScore: {
    fontSize: Font.md,
    fontWeight: 600,
    color: Colors.accentBlueBright,
    fontVariantNumeric: 'tabular-nums',
  },
  fcBadge: {
    fontSize: Font.xs,
    fontWeight: 700,
    color: Colors.gold,
    backgroundColor: Colors.goldBg,
    padding: `${Gap.xs}px ${Gap.sm}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.goldStroke}`,
  },
  viewAll: {
    fontSize: Font.sm,
    color: Colors.accentBlue,
    textAlign: 'center' as const,
    paddingTop: Gap.md,
    marginTop: Gap.sm,
    borderTop: `1px solid ${Colors.borderSubtle}`,
  },
  playerScoreSection: {
    marginTop: 'auto',
    padding: Gap.md,
    backgroundColor: Colors.accentPurpleDark,
    borderRadius: Radius.xs,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.sm,
  },
  playerScoreLabel: {
    fontSize: Font.xs,
    fontWeight: 600,
    color: Colors.accentPurple,
    textTransform: 'uppercase' as const,
    letterSpacing: 0.5,
  },
  playerScoreRow: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
  },
  playerRank: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
    width: 56,
    flexShrink: 0,
  },
  playerScoreValue: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.accentBlueBright,
    fontVariantNumeric: 'tabular-nums',
  },
  playerAccuracy: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  playerStars: {
    fontSize: Font.sm,
    color: Colors.gold,
  },
  notPlayed: {
    fontSize: Font.lg,
    color: Colors.textMuted,
    fontStyle: 'italic' as const,
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
