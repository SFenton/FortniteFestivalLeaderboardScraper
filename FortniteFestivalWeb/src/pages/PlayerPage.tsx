import { useEffect, useState, useCallback } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useFestival } from '../contexts/FestivalContext';
import { useSyncStatus } from '../hooks/useSyncStatus';
import { api } from '../api/client';
import {
  INSTRUMENT_KEYS,
  INSTRUMENT_LABELS,
  type InstrumentKey,
  type PlayerResponse,
  type PlayerScore,
  type Song,
} from '../models';
import { Colors, Font, Gap, Radius, Layout, MaxWidth } from '../theme';

export default function PlayerPage() {
  const { accountId } = useParams<{ accountId: string }>();
  const {
    state: { songs },
  } = useFestival();

  const [data, setData] = useState<PlayerResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const { isSyncing, status, progress, justCompleted, clearCompleted } =
    useSyncStatus(accountId);

  const fetchPlayer = useCallback(async () => {
    if (!accountId) return;
    setLoading(true);
    setError(null);
    try {
      const res = await api.getPlayer(accountId);
      setData(res);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load player');
    } finally {
      setLoading(false);
    }
  }, [accountId]);

  useEffect(() => {
    void fetchPlayer();
  }, [fetchPlayer]);

  // Auto-reload when sync completes
  useEffect(() => {
    if (justCompleted) {
      clearCompleted();
      void fetchPlayer();
    }
  }, [justCompleted, clearCompleted, fetchPlayer]);

  if (loading) return <div style={styles.center}>Loading player…</div>;
  if (error) return <div style={styles.centerError}>{error}</div>;
  if (!data) return <div style={styles.center}>Player not found</div>;

  const songMap = new Map(songs.map((s) => [s.songId, s]));
  const byInstrument = groupByInstrument(data.scores);
  const overallStats = computeOverallStats(data.scores);

  return (
    <div style={styles.page}>
      <div style={styles.container}>
        <h1 style={styles.playerName}>{data.displayName}</h1>
        <p style={styles.subtitle}>
          {data.totalScores.toLocaleString()} total scores
        </p>

        {/* Sync banner */}
        {isSyncing && (
          <div style={styles.syncBanner}>
            <div style={styles.syncSpinner} />
            <div>
              <div style={styles.syncTitle}>Syncing Your Data</div>
              <div style={styles.syncSubtitle}>
                Once {data.displayName}'s scores have been synced, more data will appear here.
              </div>
              {status === 'in_progress' && progress > 0 && (
                <div style={styles.syncProgressOuter}>
                  <div
                    style={{
                      ...styles.syncProgressInner,
                      width: `${Math.round(progress * 100)}%`,
                    }}
                  />
                </div>
              )}
            </div>
          </div>
        )}

        {/* Overall summary */}
        <div style={styles.summaryGrid}>
          <StatBox label="Total Score" value={overallStats.totalScore.toLocaleString()} />
          <StatBox label="Songs Played" value={overallStats.songsPlayed.toLocaleString()} />
          <StatBox label="Full Combos" value={`${overallStats.fcCount} (${overallStats.fcPercent}%)`} />
          <StatBox label="Gold Stars" value={overallStats.goldStarCount.toLocaleString()} />
          <StatBox label="Avg Accuracy" value={formatAccuracy(overallStats.avgAccuracy)} />
          <StatBox label="Best Rank" value={overallStats.bestRank > 0 ? `#${overallStats.bestRank.toLocaleString()}` : '—'} />
        </div>

        {/* Per-instrument cards */}
        <h2 style={styles.sectionTitle}>Instrument Statistics</h2>
        <p style={styles.sectionDesc}>A quick look at your overall Festival statistics per instrument.</p>
        <div style={styles.instrumentGrid}>
          {INSTRUMENT_KEYS.map((inst) => {
            const scores = byInstrument.get(inst);
            if (!scores || scores.length === 0) return null;
            return (
              <InstrumentStatsCard
                key={inst}
                instrument={inst}
                scores={scores}
                totalSongs={songs.length}
              />
            );
          })}
        </div>

        {/* Top Songs Per Instrument */}
        <h2 style={{ ...styles.sectionTitle, marginTop: Gap.section * 2 }}>Top Songs Per Instrument</h2>
        <p style={styles.sectionDesc}>Your best and worst competitive songs per instrument, sorted by percentile.</p>
        <div style={styles.instrumentGrid}>
          {INSTRUMENT_KEYS.map((inst) => {
            const scores = byInstrument.get(inst);
            if (!scores || scores.length === 0) return null;
            const withPct = scores.filter(
              (s) => s.rank > 0 && (s.totalEntries ?? 0) > 0,
            );
            if (withPct.length === 0) return null;
            const sorted = withPct
              .slice()
              .sort(
                (a, b) =>
                  a.rank / a.totalEntries! - b.rank / b.totalEntries!,
              );
            const topScores = sorted.slice(0, 5);
            const bottomScores = sorted.length > 5
              ? sorted.slice(-5).reverse()
              : [];
            return (
              <div key={inst} style={{ display: 'contents' }}>
                <TopSongsCard
                  instrument={inst}
                  title="Top Five Songs"
                  description={`Your best five songs for ${INSTRUMENT_LABELS[inst]}.`}
                  scores={topScores}
                  songMap={songMap}
                />
                {bottomScores.length > 0 && (
                  <TopSongsCard
                    instrument={inst}
                    title="Bottom Five Songs"
                    description={`Your worst five songs for ${INSTRUMENT_LABELS[inst]}.`}
                    scores={bottomScores}
                    songMap={songMap}
                  />
                )}
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}

function StatBox({ label, value }: { label: string; value: string }) {
  return (
    <div style={styles.statBox}>
      <span style={styles.statValue}>{value}</span>
      <span style={styles.statLabel}>{label}</span>
    </div>
  );
}

function InstrumentStatsCard({
  instrument,
  scores,
  totalSongs,
}: {
  instrument: InstrumentKey;
  scores: PlayerScore[];
  totalSongs: number;
}) {
  const stats = computeInstrumentStats(scores, totalSongs);

  return (
    <div style={styles.instCard}>
      <div style={styles.instCardHeader}>
        <span style={styles.instCardTitle}>
          {INSTRUMENT_LABELS[instrument]}
        </span>
        <span style={styles.instCardSubtitle}>
          {stats.songsPlayed} of {totalSongs} songs played ({stats.completionPercent}%)
        </span>
      </div>
      <div style={styles.instCardBody}>
        {/* Stats grid — matches mobile's 9 stats exactly */}
        <div style={styles.statsGrid}>
          <MiniStat label="FCs" value={`${stats.fcCount} (${stats.fcPercent}%)`} />
          <MiniStat label="Gold Stars" value={stats.goldStarCount.toString()} />
          <MiniStat label="5 Stars" value={stats.fiveStarCount.toString()} />
          <MiniStat label="4 Stars" value={stats.fourStarCount.toString()} />
          <MiniStat label="Average Accuracy" value={formatAccuracy(stats.avgAccuracy)} />
          <MiniStat label="Best Accuracy" value={formatAccuracy(stats.bestAccuracy)} />
          <MiniStat label="Average Stars" value={stats.averageStars > 0 ? stats.averageStars.toFixed(2) : '—'} />
          <MiniStat label="Best Rank" value={stats.bestRank > 0 ? `#${stats.bestRank.toLocaleString()}` : '—'} />
          <MiniStat label="Percentile" value={stats.overallPercentile} />
          <MiniStat label="Percentile (Songs Played)" value={stats.avgPercentile} />
        </div>

        {/* Percentile distribution */}
        {stats.songsPlayed > 0 && (
          <PercentileBar stats={stats} />
        )}
      </div>
    </div>
  );
}

function MiniStat({ label, value }: { label: string; value: string }) {
  return (
    <div style={styles.miniStat}>
      <span style={styles.miniStatLabel}>{label}</span>
      <span style={styles.miniStatValue}>{value}</span>
    </div>
  );
}

function PercentileBar({ stats }: { stats: InstrumentStats }) {
  const buckets = [
    { label: 'Top 1%', count: stats.top1Pct, color: '#27ae60' },
    { label: 'Top 5%', count: stats.top5Pct, color: '#2ecc71' },
    { label: 'Top 10%', count: stats.top10Pct, color: '#f1c40f' },
    { label: 'Top 25%', count: stats.top25Pct, color: '#e67e22' },
    { label: 'Top 50%', count: stats.top50Pct, color: '#e74c3c' },
    { label: '>50%', count: stats.below50Pct, color: '#7f8c8d' },
  ];
  const total = buckets.reduce((s, b) => s + b.count, 0);
  if (total === 0) return null;

  return (
    <div style={styles.percentileSection}>
      <div style={styles.percentileBar}>
        {buckets.map(
          (b) =>
            b.count > 0 && (
              <div
                key={b.label}
                style={{
                  flex: b.count,
                  height: 8,
                  backgroundColor: b.color,
                }}
              />
            ),
        )}
      </div>
      <div style={styles.percentileLegend}>
        {buckets
          .filter((b) => b.count > 0)
          .map((b) => (
            <span key={b.label} style={styles.legendItem}>
              <span
                style={{
                  ...styles.legendDot,
                  backgroundColor: b.color,
                }}
              />
              {b.label}: {b.count}
            </span>
          ))}
      </div>
    </div>
  );
}

function TopSongsCard({
  instrument,
  title,
  description,
  scores,
  songMap,
}: {
  instrument: InstrumentKey;
  title: string;
  description: string;
  scores: PlayerScore[];
  songMap: Map<string, Song>;
}) {
  return (
    <div style={styles.instCard}>
      <div style={styles.instCardHeader}>
        <div>
          <span style={styles.instCardTitle}>
            {title}
          </span>
          <div style={styles.catDesc}>
            {description}
          </div>
        </div>
        <span style={styles.instCardSubtitle}>
          {INSTRUMENT_LABELS[instrument]}
        </span>
      </div>
      <div style={styles.instCardBody}>
        {scores.map((s) => {
          const song = songMap.get(s.songId);
          const pct =
            s.rank > 0 && (s.totalEntries ?? 0) > 0
              ? Math.min((s.rank / s.totalEntries!) * 100, 100)
              : undefined;
          const isTop5 = pct != null && pct <= 5;
          return (
            <Link
              key={s.songId}
              to={`/songs/${s.songId}`}
              style={styles.topSongRow}
            >
              {song?.albumArt ? (
                <img
                  src={song.albumArt}
                  alt=""
                  style={styles.topSongThumb}
                  loading="lazy"
                />
              ) : (
                <div
                  style={{
                    ...styles.topSongThumb,
                    backgroundColor: Colors.purplePlaceholder,
                  }}
                />
              )}
              <div style={styles.topSongText}>
                <span style={styles.topSongName}>
                  {song?.title ?? s.songId.slice(0, 8)}
                </span>
                <span style={styles.topSongArtist}>
                  {song?.artist ?? ''}
                </span>
              </div>
              <div style={styles.topSongRight}>
                {pct != null && (
                  <span
                    style={{
                      ...styles.percentilePill,
                      ...(isTop5 ? styles.percentilePillGold : {}),
                    }}
                  >
                    Top {Math.max(0.01, pct).toFixed(2)}%
                  </span>
                )}
              </div>
            </Link>
          );
        })}
      </div>
    </div>
  );
}

// ─── Computation helpers ────────────────────────────────────

type InstrumentStats = {
  songsPlayed: number;
  completionPercent: string;
  fcCount: number;
  fcPercent: string;
  goldStarCount: number;
  fiveStarCount: number;
  fourStarCount: number;
  averageStars: number;
  avgAccuracy: number;
  bestAccuracy: number;
  avgScore: number;
  bestRank: number;
  overallPercentile: string;
  avgPercentile: string;
  top1Pct: number;
  top5Pct: number;
  top10Pct: number;
  top25Pct: number;
  top50Pct: number;
  below50Pct: number;
};

function computeInstrumentStats(
  scores: PlayerScore[],
  totalSongs: number,
): InstrumentStats {
  const n = scores.length;
  const fcCount = scores.filter((s) => s.isFullCombo).length;
  const goldStars = scores.filter((s) => (s.stars ?? 0) >= 6).length;
  const fiveStars = scores.filter((s) => (s.stars ?? 0) === 5).length;
  const fourStars = scores.filter((s) => (s.stars ?? 0) === 4).length;

  const starsWithScore = scores.filter((s) => (s.stars ?? 0) > 0);
  const averageStars =
    starsWithScore.length > 0
      ? starsWithScore.reduce((a, s) => a + (s.stars ?? 0), 0) / starsWithScore.length
      : 0;

  const accuracies = scores
    .map((s) => s.accuracy ?? 0)
    .filter((a) => a > 0);
  const avgAcc =
    accuracies.length > 0
      ? accuracies.reduce((a, b) => a + b, 0) / accuracies.length
      : 0;
  const bestAcc = accuracies.length > 0 ? Math.max(...accuracies) : 0;

  const avgScore = n > 0 ? scores.reduce((a, b) => a + b.score, 0) / n : 0;

  const ranks = scores.map((s) => s.rank).filter((r) => r > 0);
  const bestRank = ranks.length > 0 ? Math.min(...ranks) : 0;

  // Compute percentile as rank / totalEntries for each score
  const percentiled = scores
    .filter((s) => s.rank > 0 && (s.totalEntries ?? 0) > 0)
    .map((s) => ({
      pct: s.rank / s.totalEntries!,   // 0..1 (lower is better)
      weight: s.totalEntries!,
    }));

  // Percentile calculations
  let overallPercentile = '—';   // avg across ALL songs (unplayed = 100%)
  let avgPercentile = '—';       // avg across only songs played
  if (percentiled.length > 0) {
    // Songs Played: simple average across songs with data
    const avgPlayed = (percentiled.reduce((a, v) => a + v.pct, 0) / percentiled.length) * 100;
    avgPercentile = `Top ${Math.max(0.01, Math.min(100, avgPlayed)).toFixed(0)}%`;

    // Overall: unplayed songs count as 100% (worst)
    const unplayedCount = totalSongs - n;
    const totalPct = percentiled.reduce((a, v) => a + v.pct, 0) + unplayedCount; // unplayed × 1.0
    const overall = (totalPct / totalSongs) * 100;
    overallPercentile = `Top ${Math.max(0.01, Math.min(100, overall)).toFixed(0)}%`;
  }

  // Percentile distribution buckets
  let top1 = 0,
    top5 = 0,
    top10 = 0,
    top25 = 0,
    top50 = 0,
    below50 = 0;
  for (const { pct } of percentiled) {
    const pctVal = pct * 100;
    if (pctVal <= 1) top1++;
    else if (pctVal <= 5) top5++;
    else if (pctVal <= 10) top10++;
    else if (pctVal <= 25) top25++;
    else if (pctVal <= 50) top50++;
    else below50++;
  }

  return {
    songsPlayed: n,
    completionPercent:
      totalSongs > 0 ? ((n / totalSongs) * 100).toFixed(1) : '0',
    fcCount,
    fcPercent: n > 0 ? ((fcCount / n) * 100).toFixed(1) : '0',
    goldStarCount: goldStars,
    fiveStarCount: fiveStars,
    fourStarCount: fourStars,
    averageStars,
    avgAccuracy: avgAcc,
    bestAccuracy: bestAcc,
    avgScore,
    bestRank,
    overallPercentile,
    avgPercentile,
    top1Pct: top1,
    top5Pct: top5,
    top10Pct: top10,
    top25Pct: top25,
    top50Pct: top50,
    below50Pct: below50,
  };
}

function computeOverallStats(scores: PlayerScore[]) {
  const uniqueSongs = new Set(scores.map((s) => s.songId));
  const fcCount = scores.filter((s) => s.isFullCombo).length;
  const goldStars = scores.filter((s) => (s.stars ?? 0) >= 6).length;
  const totalScore = scores.reduce((a, b) => a + b.score, 0);
  const accuracies = scores
    .map((s) => s.accuracy ?? 0)
    .filter((a) => a > 0);
  const avgAcc =
    accuracies.length > 0
      ? accuracies.reduce((a, b) => a + b, 0) / accuracies.length
      : 0;
  const ranks = scores.map((s) => s.rank).filter((r) => r > 0);
  const bestRank = ranks.length > 0 ? Math.min(...ranks) : 0;

  return {
    totalScore,
    songsPlayed: uniqueSongs.size,
    fcCount,
    fcPercent:
      scores.length > 0
        ? ((fcCount / scores.length) * 100).toFixed(1)
        : '0',
    goldStarCount: goldStars,
    avgAccuracy: avgAcc,
    bestRank,
  };
}

function groupByInstrument(scores: PlayerScore[]) {
  const map = new Map<InstrumentKey, PlayerScore[]>();
  for (const s of scores) {
    const key = s.instrument as InstrumentKey;
    if (!map.has(key)) map.set(key, []);
    map.get(key)!.push(s);
  }
  return map;
}

function formatAccuracy(val: number): string {
  if (val <= 0) return '—';
  return `${(val / 10000).toFixed(2)}%`;
}

// ─── Styles ─────────────────────────────────────────────────

const styles: Record<string, React.CSSProperties> = {
  page: {
    minHeight: '100vh',
    backgroundColor: Colors.backgroundApp,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
  },
  container: {
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px`,
  },
  playerName: {
    fontSize: 28,
    fontWeight: 700,
    marginBottom: Gap.xs,
  },
  subtitle: {
    fontSize: Font.md,
    color: Colors.textSubtle,
    marginBottom: Gap.section,
  },
  sectionTitle: {
    fontSize: Font.xl,
    fontWeight: 800,
    color: Colors.textPrimary,
    marginBottom: Gap.xs,
    marginTop: 0,
  },
  sectionDesc: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
    marginBottom: Gap.xl,
    marginTop: 0,
  },
  catDesc: {
    fontSize: Font.xs,
    color: Colors.textSecondary,
    marginTop: Gap.xs,
    opacity: 0.85,
  },
  syncBanner: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `${Gap.xl}px ${Gap.section}px`,
    backgroundColor: Colors.accentPurpleDark,
    border: `1px solid ${Colors.borderPrimary}`,
    borderRadius: Radius.lg,
    marginBottom: Gap.section,
  },
  syncSpinner: {
    width: 24,
    height: 24,
    border: '3px solid rgba(255,255,255,0.15)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
    flexShrink: 0,
  },
  syncTitle: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.textPrimary,
    marginBottom: Gap.xs,
  },
  syncSubtitle: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  syncProgressOuter: {
    marginTop: Gap.md,
    height: 6,
    backgroundColor: 'rgba(255,255,255,0.1)',
    borderRadius: 3,
    overflow: 'hidden',
  },
  syncProgressInner: {
    height: '100%',
    backgroundColor: Colors.accentPurple,
    borderRadius: 3,
    transition: 'width 0.3s ease',
  },
  // Overall summary
  summaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))',
    gap: Gap.md,
    marginBottom: Gap.section * 1.5,
  },
  statBox: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    padding: `${Gap.xl}px ${Gap.md}px`,
    backgroundColor: Colors.backgroundCard,
    border: `1px solid ${Colors.borderSubtle}`,
    borderRadius: Radius.md,
  },
  statValue: {
    fontSize: Font.xl,
    fontWeight: 700,
    color: Colors.accentBlueBright,
    marginBottom: Gap.xs,
  },
  statLabel: {
    fontSize: Font.xs,
    color: Colors.textTertiary,
    textTransform: 'uppercase' as const,
    letterSpacing: 0.5,
  },
  // Per-instrument cards
  instrumentGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(420px, 1fr))',
    gap: Gap.xl,
  },
  instCard: {
    backgroundColor: Colors.backgroundCard,
    border: `1px solid ${Colors.borderSubtle}`,
    borderRadius: Radius.lg,
    overflow: 'hidden',
  },
  instCardHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: `${Gap.md}px ${Gap.xl}px`,
    backgroundColor: Colors.accentPurpleDark,
  },
  instCardTitle: {
    fontSize: Font.lg,
    fontWeight: 600,
  },
  instCardSubtitle: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  instCardBody: {
    padding: Gap.xl,
  },
  // Stats mini-grid
  statsGrid: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: `${Gap.sm}px ${Gap.xl}px`,
    marginBottom: Gap.xl,
  },
  miniStat: {
    display: 'flex',
    justifyContent: 'space-between',
    padding: `${Gap.xs}px 0`,
  },
  miniStatLabel: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
  },
  miniStatValue: {
    fontSize: Font.sm,
    fontWeight: 600,
    color: Colors.textPrimary,
  },
  // Percentile bar
  percentileSection: {
    marginBottom: Gap.xl,
  },
  percentileBar: {
    display: 'flex',
    borderRadius: 4,
    overflow: 'hidden',
    marginBottom: Gap.sm,
  },
  percentileLegend: {
    display: 'flex',
    flexWrap: 'wrap' as const,
    gap: `${Gap.xs}px ${Gap.xl}px`,
  },
  legendItem: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.sm,
    fontSize: Font.xs,
    color: Colors.textTertiary,
  },
  legendDot: {
    width: 8,
    height: 8,
    borderRadius: '50%',
    flexShrink: 0,
  },
  // Top songs
  topSongs: {
    borderTop: `1px solid ${Colors.borderSubtle}`,
    paddingTop: Gap.xl,
  },
  topSongsTitle: {
    fontSize: Font.sm,
    fontWeight: 600,
    color: Colors.textSecondary,
    textTransform: 'uppercase' as const,
    letterSpacing: 0.5,
    display: 'block',
    marginBottom: Gap.md,
  },
  topSongRow: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    padding: `${Gap.sm}px 0`,
    textDecoration: 'none',
    color: 'inherit',
  },
  topSongThumb: {
    width: 32,
    height: 32,
    borderRadius: Radius.xs,
    objectFit: 'cover' as const,
    flexShrink: 0,
  },
  topSongText: {
    flex: 1,
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column' as const,
  },
  topSongName: {
    fontSize: Font.sm,
    fontWeight: 500,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  topSongArtist: {
    fontSize: Font.xs,
    color: Colors.textMuted,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  topSongRight: {
    textAlign: 'right' as const,
    flexShrink: 0,
  },
  topSongScore: {
    fontSize: Font.sm,
    fontWeight: 600,
    color: Colors.accentBlueBright,
    fontVariantNumeric: 'tabular-nums',
    display: 'block',
  },
  topSongMeta: {
    fontSize: Font.xs,
    color: Colors.gold,
  },
  percentilePill: {
    fontSize: Font.xs,
    fontWeight: 600,
    color: Colors.textSecondary,
    backgroundColor: 'rgba(255,255,255,0.1)',
    padding: `${Gap.xs}px ${Gap.md}px`,
    borderRadius: Radius.xs,
    display: 'inline-block',
    marginBottom: 2,
  },
  percentilePillGold: {
    color: Colors.gold,
    backgroundColor: Colors.goldBg,
    border: `1px solid ${Colors.goldStroke}`,
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
  centerError: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '100vh',
    color: Colors.statusRed,
    backgroundColor: Colors.backgroundApp,
    fontSize: Font.lg,
  },
};
