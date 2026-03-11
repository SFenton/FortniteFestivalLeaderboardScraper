import { useEffect, useState, useCallback, useRef, type CSSProperties } from 'react';
import { useParams, Link, useNavigate, useLocation } from 'react-router-dom';
import { formatPercentile } from '../utils/formatPercentile';
import { useFestival } from '../contexts/FestivalContext';
import { usePlayerData } from '../contexts/PlayerDataContext';
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
import { Colors, Font, Gap, Radius, Layout, MaxWidth, goldFill, goldOutline, goldOutlineSkew } from '../theme';
import { InstrumentIcon } from '../components/InstrumentIcons';
import { useSettings, isInstrumentVisible } from '../contexts/SettingsContext';
import { loadSongSettings, saveSongSettings } from '../components/songSettings';

/** Wrapper that fades in via CSS animation, then strips the animation styles
 *  so that `opacity` is no longer set by the animation system.  This prevents
 *  the browser from keeping a compositing group alive, which would break
 *  `backdrop-filter: blur()` on child elements. */
function FadeInDiv({ delay, children, style }: { delay: number; children: React.ReactNode; style?: CSSProperties }) {
  const ref = useRef<HTMLDivElement>(null);
  const handleEnd = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  return (
    <div
      ref={ref}
      style={{ opacity: 0, animation: `fadeInUp 400ms ease-out ${delay}ms forwards`, ...style }}
      onAnimationEnd={handleEnd}
    >
      {children}
    </div>
  );
}

export default function PlayerPage({ accountId: propAccountId }: { accountId?: string } = {}) {
  const params = useParams<{ accountId: string }>();
  const accountId = propAccountId ?? params.accountId;
  const {
    state: { songs },
  } = useFestival();

  // Use cached context data when viewing the tracked player (statistics tab)
  const ctx = usePlayerData();
  const isTrackedPlayer = !!propAccountId && ctx.playerData?.accountId === propAccountId;

  // Local state for when viewing an arbitrary player via URL
  const [localData, setLocalData] = useState<PlayerResponse | null>(null);
  const [localLoading, setLocalLoading] = useState(!isTrackedPlayer);
  const [localError, setLocalError] = useState<string | null>(null);
  const hasDataRef = useRef(false);

  const { isSyncing: localSyncing, phase: localPhase, backfillProgress: localBfProg, historyProgress: localHrProg, justCompleted, clearCompleted } =
    useSyncStatus(!isTrackedPlayer ? accountId : undefined);

  const fetchPlayer = useCallback(async () => {
    if (!accountId || isTrackedPlayer) return;
    if (!hasDataRef.current) setLocalLoading(true);
    setLocalError(null);
    try {
      const res = await api.getPlayer(accountId);
      setLocalData(res);
      hasDataRef.current = true;
    } catch (e) {
      setLocalError(e instanceof Error ? e.message : 'Failed to load player');
    } finally {
      setLocalLoading(false);
    }
  }, [accountId, isTrackedPlayer]);

  useEffect(() => {
    if (isTrackedPlayer) return;
    hasDataRef.current = false;
    void fetchPlayer();
  }, [fetchPlayer, isTrackedPlayer]);

  useEffect(() => {
    if (justCompleted) {
      clearCompleted();
      void fetchPlayer();
    }
  }, [justCompleted, clearCompleted, fetchPlayer]);

  // Resolve effective values: context for tracked player, local for others
  const data = isTrackedPlayer ? ctx.playerData : localData;
  const loading = isTrackedPlayer ? ctx.playerLoading : localLoading;
  const error = isTrackedPlayer ? ctx.playerError : localError;
  const isSyncing = isTrackedPlayer ? ctx.isSyncing : localSyncing;
  const phase = isTrackedPlayer ? ctx.syncPhase : localPhase;
  const backfillProgress = isTrackedPlayer ? ctx.backfillProgress : localBfProg;
  const historyProgress = isTrackedPlayer ? ctx.historyProgress : localHrProg;

  if (loading) return <div style={styles.center}><div style={styles.arcSpinner} /></div>;
  if (error) return <div style={styles.centerError}>{error}</div>;
  if (!data) return <div style={styles.center}>Player not found</div>;

  return <PlayerContent data={data} songs={songs} isSyncing={isSyncing} phase={phase} backfillProgress={backfillProgress} historyProgress={historyProgress} isTrackedPlayer={isTrackedPlayer} />;
}

function PlayerContent({
  data,
  songs,
  isSyncing,
  phase: syncPhase,
  backfillProgress,
  historyProgress,
  isTrackedPlayer,
}: {
  data: PlayerResponse;
  songs: Song[];
  isSyncing: boolean;
  phase: string | null;
  backfillProgress: number;
  historyProgress: number;
  isTrackedPlayer: boolean;
}) {
  const { settings } = useSettings();
  const location = useLocation();

  // For the tracked player, filter scores by visible instruments;
  // for other players, always show all data.
  const effectiveScores = isTrackedPlayer
    ? data.scores.filter(s => isInstrumentVisible(settings, s.instrument as InstrumentKey))
    : data.scores;
  const visibleKeys = isTrackedPlayer
    ? INSTRUMENT_KEYS.filter(k => isInstrumentVisible(settings, k))
    : INSTRUMENT_KEYS;

  const songMap = new Map(songs.map((s) => [s.songId, s]));
  const byInstrument = groupByInstrument(effectiveScores);
  const overallStats = computeOverallStats(effectiveScores);

  // Build a flat list of stagger-able sections so we can assign sequential delays
  const sections: React.ReactNode[] = [];

  // 0: Sync banner (if showing)
  if (isSyncing) {
    sections.push(
      <div key="sync" style={styles.syncBanner}>
        <div style={styles.syncSpinner} />
        <div style={{ flex: 1 }}>
          <div style={styles.syncTitle}>
            {syncPhase === 'backfill' ? 'Syncing Data' : 'Building Score History'}
          </div>
          <div style={styles.syncSubtitle}>
            {syncPhase === 'backfill'
              ? `Syncing ${data.displayName}'s scores…`
              : `Reconstructing ${data.displayName}'s score history across seasons…`}
          </div>
          {syncPhase === 'backfill' && backfillProgress > 0 && (
            <div style={{ marginTop: Gap.md }}>
              <div style={styles.syncProgressLabel}>
                <span>Syncing scores</span>
                <span>{(backfillProgress * 100).toFixed(1)}%</span>
              </div>
              <div style={styles.syncProgressOuter}>
                <div
                  style={{
                    ...styles.syncProgressInner,
                    width: `${Math.round(backfillProgress * 100)}%`,
                  }}
                />
              </div>
            </div>
          )}
          {syncPhase === 'history' && (
            <>
              <div style={{ marginTop: Gap.md }}>
                <div style={styles.syncProgressLabel}>
                  <span>Syncing scores</span>
                  <span>100.0%</span>
                </div>
                <div style={styles.syncProgressOuter}>
                  <div style={{ ...styles.syncProgressInner, width: '100%' }} />
                </div>
              </div>
              {historyProgress > 0 && (
                <div style={{ marginTop: Gap.sm }}>
                  <div style={styles.syncProgressLabel}>
                    <span>Building history</span>
                    <span>{(historyProgress * 100).toFixed(1)}%</span>
                  </div>
                  <div style={styles.syncProgressOuter}>
                    <div
                      style={{
                        ...styles.syncProgressInner,
                        width: `${Math.round(historyProgress * 100)}%`,
                      }}
                    />
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      </div>,
    );
  }

  // 2: Overall summary
  const overallAccColor = overallStats.avgAccuracy > 0
    ? (overallStats.avgAccuracy / 10000 >= 100 && overallStats.fcPercent === '100.0'
        ? Colors.gold
        : accuracyColor(overallStats.avgAccuracy / 10000))
    : undefined;
  const overallSongsAllPlayed = overallStats.songsPlayed >= songs.length && songs.length > 0;
  const overallFcIs100 = overallStats.fcPercent === '100.0';
  const overallFcValue = overallFcIs100
    ? overallStats.fcCount.toLocaleString()
    : `${overallStats.fcCount} (${formatClamped(parseFloat(overallStats.fcPercent))}%)`;
  sections.push(
    <div key="summary" style={styles.summaryGrid}>
      <StatBox label="Songs Played" value={overallStats.songsPlayed.toLocaleString()} color={overallSongsAllPlayed ? Colors.statusGreen : undefined} />
      <StatBox label="Full Combos" value={overallFcValue} color={overallFcIs100 ? Colors.gold : undefined} />
      <StatBox label="Gold Stars" value={overallStats.goldStarCount.toLocaleString()} color={Colors.gold} />
      <StatBox label="Avg Accuracy" value={overallStats.avgAccuracy > 0 ? formatClamped(overallStats.avgAccuracy / 10000) + '%' : '—'} color={overallAccColor} />
      <StatBox label="Best Rank" value={overallStats.bestRank > 0 ? `#${overallStats.bestRank.toLocaleString()}` : '—'} to={overallStats.bestRankSongId ? `/songs/${overallStats.bestRankSongId}?instrument=${encodeURIComponent(overallStats.bestRankInstrument!)}` : undefined} state={{ backTo: location.pathname }} />
    </div>,
  );

  // 3: Instrument Statistics heading
  sections.push(
    <div key="inst-heading">
      <h2 style={styles.sectionTitle}>Instrument Statistics</h2>
      <p style={styles.sectionDesc}>A quick look at your overall Festival statistics per instrument.</p>
    </div>,
  );

  // 4+: Each instrument stats card (inside a grid)
  const instCards: { inst: InstrumentKey; scores: PlayerScore[] }[] = [];
  for (const inst of visibleKeys) {
    const scores = byInstrument.get(inst);
    if (!scores || scores.length === 0) continue;
    instCards.push({ inst, scores });
  }
  // Push each card as its own section so it gets its own stagger delay
  // but wrap them in a grid parent. We use a "grid-start" marker and
  // "grid-end" marker approach — or simpler: push one grid section containing
  // all cards, each with an inline stagger using the section index as base.
  const instGridBaseIndex = sections.length;
  sections.push(
    <div key="inst-grid" style={styles.instrumentGrid}>
      {instCards.map(({ inst, scores }, i) => (
        <FadeInDiv key={inst} delay={(instGridBaseIndex + i) * 125}>
          <InstrumentStatsCard
            instrument={inst}
            scores={scores}
            totalSongs={songs.length}
          />
        </FadeInDiv>
      ))}
    </div>,
  );

  // Top Songs heading
  sections.push(
    <div key="top-heading" style={{ marginTop: Gap.section * 2 }}>
      <h2 style={styles.sectionTitle}>Top Songs Per Instrument</h2>
      <p style={styles.sectionDesc}>Your best and worst competitive songs per instrument, sorted by percentile.</p>
    </div>,
  );

  // Top/Bottom songs cards per instrument (inside a grid)
  const topBottomCards: React.ReactNode[] = [];
  for (const inst of visibleKeys) {
    const scores = byInstrument.get(inst);
    if (!scores || scores.length === 0) continue;
    const withPct = scores.filter(
      (s) => s.rank > 0 && (s.totalEntries ?? 0) > 0,
    );
    if (withPct.length === 0) continue;
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
    topBottomCards.push(
      <TopSongsCard
        key={`top-${inst}`}
        instrument={inst}
        title="Top Five Songs"
        description={`Your best five songs for ${INSTRUMENT_LABELS[inst]}.`}
        scores={topScores}
        songMap={songMap}
      />,
    );
    if (bottomScores.length > 0) {
      topBottomCards.push(
        <TopSongsCard
          key={`bottom-${inst}`}
          instrument={inst}
          title="Bottom Five Songs"
          description={`Your worst five songs for ${INSTRUMENT_LABELS[inst]}.`}
          scores={bottomScores}
          songMap={songMap}
        />,
      );
    }
  }
  const topGridBaseIndex = sections.length;
  sections.push(
    <div key="top-grid" style={styles.instrumentGrid}>
      {topBottomCards.map((card, i) => (
        <FadeInDiv key={i} delay={(topGridBaseIndex + i) * 125}>
          {card}
        </FadeInDiv>
      ))}
    </div>,
  );

  return (
    <div style={styles.page}>
      <div style={styles.container}>
        {sections.map((section, i) => (
          <FadeInDiv key={i} delay={i * 125}>
            {section}
          </FadeInDiv>
        ))}
      </div>
    </div>
  );
}

function StatBox({ label, value, color, to, state }: { label: string; value: React.ReactNode; color?: string; to?: string; state?: Record<string, string> }) {
  const inner = (
    <div style={styles.statBox}>
      <span style={{ ...styles.statValue, ...(color ? { color } : {}) }}>{value}</span>
      <span style={styles.statLabel}>{label}</span>
    </div>
  );
  if (to) return <Link to={to} state={state} style={{ textDecoration: 'none', color: 'inherit' }}>{inner}</Link>;
  return inner;
}

function GoldStars() {
  return (
    <span style={{ display: 'inline-flex', gap: 2 }}>
      {Array.from({ length: 5 }, (_, i) => (
        <img key={i} src="/app/star_gold.png" alt="★" width={18} height={18} />
      ))}
    </span>
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

  const cards: { label: string; value: React.ReactNode; color?: string; to?: string }[] = [];
  if (stats.songsPlayed > 0) cards.push({ label: 'Songs Played', value: stats.songsPlayed.toLocaleString(), color: stats.songsPlayed >= totalSongs ? Colors.statusGreen : undefined });
  if (stats.fcCount > 0) cards.push({ label: 'FCs', value: stats.fcPercent === '100.0' ? stats.fcCount.toLocaleString() : `${stats.fcCount} (${stats.fcPercent}%)`, color: stats.fcPercent === '100.0' ? Colors.gold : undefined });
  if (stats.goldStarCount > 0) cards.push({ label: 'Gold Stars', value: stats.goldStarCount.toLocaleString(), color: Colors.gold });
  if (stats.fiveStarCount > 0) cards.push({ label: '5 Stars', value: stats.fiveStarCount.toLocaleString() });
  if (stats.fourStarCount > 0) cards.push({ label: '4 Stars', value: stats.fourStarCount.toLocaleString() });
  if (stats.threeStarCount > 0) cards.push({ label: '3 Stars', value: stats.threeStarCount.toLocaleString() });
  if (stats.twoStarCount > 0) cards.push({ label: '2 Stars', value: stats.twoStarCount.toLocaleString() });
  if (stats.oneStarCount > 0) cards.push({ label: '1 Star', value: stats.oneStarCount.toLocaleString() });
  const accPct = stats.avgAccuracy / 10000;
  const isGoldAcc = accPct >= 100 && stats.fcPercent === '100.0';
  const accColor = stats.avgAccuracy > 0 ? (isGoldAcc ? Colors.gold : accuracyColor(accPct)) : undefined;
  cards.push({ label: 'Avg Accuracy', value: stats.avgAccuracy > 0 ? formatClamped(accPct) + '%' : '—', color: accColor });
  cards.push({ label: 'Avg Stars', value: stats.averageStars === 6 ? <GoldStars /> : (stats.averageStars > 0 ? formatClamped2(stats.averageStars) : '—') });
  cards.push({ label: 'Best Rank', value: stats.bestRank > 0 ? `#${stats.bestRank.toLocaleString()}` : '—', to: stats.bestRankSongId ? `/songs/${stats.bestRankSongId}?instrument=${encodeURIComponent(instrument)}` : undefined });
  const pctGold = (v: string) => /^Top [1-5]%$/.test(v) ? Colors.gold : undefined;
  cards.push({ label: 'Percentile', value: stats.overallPercentile, color: pctGold(stats.overallPercentile) });
  cards.push({ label: 'Percentile (Songs Played)', value: stats.avgPercentile, color: pctGold(stats.avgPercentile) });

  return (
    <div>
      <div style={styles.instCardHeader}>
        <InstrumentIcon instrument={instrument} size={48} />
        <span style={styles.instCardTitle}>
          {INSTRUMENT_LABELS[instrument]}
        </span>
      </div>
      <div style={styles.instSummaryGrid}>
        {cards.map((c) => (
          <StatBox key={c.label} label={c.label} value={c.value} color={c.color} to={c.to} />
        ))}
      </div>
      {stats.percentileBuckets.length > 0 && (
        <PercentileTable buckets={stats.percentileBuckets} instrument={instrument} />
      )}
    </div>
  );
}

function PercentileTable({ buckets, instrument }: { buckets: { pct: number; count: number }[]; instrument: InstrumentKey }) {
  const navigate = useNavigate();
  const thresholds = [1,2,3,4,5,10,15,20,25,30,40,50,60,70,80,90,100];

  const handleClick = (pct: number) => {
    const settings = loadSongSettings();
    // Disable all percentile buckets except the clicked one
    const percentileFilter: Record<number, boolean> = {};
    for (const t of thresholds) {
      percentileFilter[t] = t === pct;
    }
    percentileFilter[0] = false; // hide "No Score"
    saveSongSettings({
      ...settings,
      instrument,
      filters: { ...settings.filters, percentileFilter },
    });
    navigate('/songs');
  };

  return (
    <div style={styles.pctTablePanel}>
      <table style={styles.pctTable}>
        <thead>
          <tr>
            <th style={styles.pctTh}>Percentile</th>
            <th style={{ ...styles.pctTh, textAlign: 'right' }}>Songs</th>
          </tr>
        </thead>
        <tbody>
          {buckets.map((b) => {
            const isTop1 = b.pct <= 1;
            const isGold = b.pct <= 5;
            const badgeStyle = isTop1
              ? styles.pctGoldBadge
              : isGold
                ? styles.pctGoldPill
                : undefined;
            return (
              <tr key={b.pct} onClick={() => handleClick(b.pct)} style={styles.pctRow}>
                <td style={styles.pctTd}>
                  {badgeStyle
                    ? <span style={badgeStyle}>Top {b.pct}%</span>
                    : <span style={styles.pctPlainLabel}>Top {b.pct}%</span>}
                </td>
                <td style={{ ...styles.pctTd, textAlign: 'right', fontWeight: 600 }}>
                  {b.count}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
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
    <div>
      <div style={styles.instCardHeader}>
        <InstrumentIcon instrument={instrument} size={48} />
        <div style={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: 48 }}>
          <span style={styles.instCardTitle}>{title}</span>
          <span style={{ ...styles.sectionDesc, margin: 0, fontSize: Font.md }}>{description}</span>
        </div>
      </div>
      <div style={styles.instCard}>
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
              to={`/songs/${s.songId}?instrument=${encodeURIComponent(instrument)}`}
              state={{ backTo: location.pathname }}
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
                    {formatPercentile(pct)}
                  </span>
                )}
              </div>
            </Link>
          );
        })}
        </div>
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
  threeStarCount: number;
  twoStarCount: number;
  oneStarCount: number;
  averageStars: number;
  avgAccuracy: number;
  bestAccuracy: number;
  avgScore: number;
  bestRank: number;
  bestRankSongId: string | null;
  overallPercentile: string;
  avgPercentile: string;
  percentileBuckets: { pct: number; count: number }[];
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
  const threeStars = scores.filter((s) => (s.stars ?? 0) === 3).length;
  const twoStars = scores.filter((s) => (s.stars ?? 0) === 2).length;
  const oneStars = scores.filter((s) => (s.stars ?? 0) === 1).length;

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

  const rankedScores = scores.filter((s) => s.rank > 0);
  const bestRank = rankedScores.length > 0 ? Math.min(...rankedScores.map((s) => s.rank)) : 0;
  const bestRankSongId = bestRank > 0 ? (rankedScores.find((s) => s.rank === bestRank)?.songId ?? null) : null;

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
    avgPercentile = formatPercentile(avgPlayed);

    // Overall: unplayed songs count as 100% (worst)
    const unplayedCount = totalSongs - n;
    const totalPct = percentiled.reduce((a, v) => a + v.pct, 0) + unplayedCount; // unplayed × 1.0
    const overall = (totalPct / totalSongs) * 100;
    overallPercentile = formatPercentile(overall);
  }

  // Percentile distribution buckets (matching filter thresholds)
  const pctThresholds = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100];
  const percentileBuckets: { pct: number; count: number }[] = [];
  for (const t of pctThresholds) {
    const prev = pctThresholds[pctThresholds.indexOf(t) - 1] ?? 0;
    let count = 0;
    for (const { pct } of percentiled) {
      const pctVal = pct * 100;
      if (pctVal > prev && pctVal <= t) count++;
    }
    if (count > 0) percentileBuckets.push({ pct: t, count });
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
    threeStarCount: threeStars,
    twoStarCount: twoStars,
    oneStarCount: oneStars,
    averageStars,
    avgAccuracy: avgAcc,
    bestAccuracy: bestAcc,
    avgScore,
    bestRank,
    bestRankSongId,
    overallPercentile,
    avgPercentile,
    percentileBuckets,
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
  const rankedScores = scores.filter((s) => s.rank > 0);
  const bestRank = rankedScores.length > 0 ? Math.min(...rankedScores.map((s) => s.rank)) : 0;
  const bestRankScore = bestRank > 0 ? rankedScores.find((s) => s.rank === bestRank) : undefined;

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
    bestRankSongId: bestRankScore?.songId ?? null,
    bestRankInstrument: bestRankScore?.instrument ?? null,
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

function formatClamped(val: number): string {
  const fixed = val.toFixed(1);
  return fixed.endsWith('.0') ? Math.round(val).toString() : fixed;
}

function formatClamped2(val: number): string {
  const fixed = val.toFixed(2);
  if (fixed.endsWith('00')) return fixed.slice(0, -3);
  if (fixed.endsWith('0')) return fixed.slice(0, -1);
  return fixed;
}

function accuracyColor(pct: number): string {
  const t = Math.min(Math.max(pct / 100, 0), 1);
  const r = Math.round(220 * (1 - t) + 46 * t);
  const g = Math.round(40 * (1 - t) + 204 * t);
  const b = Math.round(40 * (1 - t) + 113 * t);
  return `rgb(${r},${g},${b})`;
}

// ─── Styles ─────────────────────────────────────────────────

const styles: Record<string, React.CSSProperties> = {
  page: {
    minHeight: '100vh',
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
    marginTop: Gap.xs,
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
  syncProgressLabel: {
    display: 'flex',
    justifyContent: 'space-between',
    fontSize: Font.xs,
    color: Colors.textSecondary,
    marginBottom: Gap.xs,
  },
  // Overall summary
  summaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, 1fr)',
    gap: Gap.md,
    marginBottom: Gap.section * 1.5,
  },
  statBox: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    padding: `${Gap.xl}px ${Gap.md}px`,
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
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
    gap: Gap.section,
  },
  instCard: {
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
    borderRadius: Radius.lg,
    overflow: 'hidden',
  },
  instCardHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    paddingBottom: Gap.sm,
  },
  instCardTitle: {
    fontSize: Font.xl,
    fontWeight: 600,
  },
  instCardSubtitle: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  instCardBody: {
    padding: Gap.xl,
  },
  // Per-instrument stat card grid
  instSummaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, 1fr)',
    gap: Gap.md,
    marginBottom: Gap.xl,
  },
  // Percentile table
  pctTablePanel: {
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
    borderRadius: Radius.lg,
    overflow: 'hidden',
    marginBottom: Gap.xl,
  },
  pctTable: {
    width: '100%',
    borderCollapse: 'collapse' as const,
  },
  pctRow: {
    cursor: 'pointer',
    transition: 'background-color 0.15s',
  },
  pctTh: {
    padding: `${Gap.xl}px ${Gap.xl}px`,
    fontSize: Font.sm,
    fontWeight: 600,
    color: Colors.textTertiary,
    textTransform: 'uppercase' as const,
    letterSpacing: 0.5,
    borderBottom: `1px solid ${Colors.glassBorder}`,
    textAlign: 'left' as const,
  },
  pctTd: {
    padding: `${Gap.xl}px ${Gap.xl}px`,
    fontSize: Font.md,
    color: Colors.textPrimary,
    borderBottom: `1px solid ${Colors.glassBorder}`,
  },
  pctGoldBadge: goldOutlineSkew,
  pctGoldPill: goldOutline,
  pctPlainLabel: {
    padding: `${Gap.xs}px ${Gap.sm}px`,
    border: '2px solid transparent',
    display: 'inline-block',
    fontWeight: 600,
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
  percentilePillGold: goldFill,
  center: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '100vh',
  },
  arcSpinner: {
    width: 48,
    height: 48,
    border: '4px solid rgba(255,255,255,0.10)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
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
