import { useEffect, useState, useMemo, useRef, useCallback } from 'react';
import {
  ComposedChart,
  Bar,
  Line,
  XAxis,
  YAxis,
  Tooltip,
  Legend,
  ResponsiveContainer,
  CartesianGrid,
} from 'recharts';
import { api } from '../api/client';
import {
  INSTRUMENT_KEYS,
  INSTRUMENT_LABELS,
  type InstrumentKey,
  type ScoreHistoryEntry,
} from '../models';
import { InstrumentIcon } from './InstrumentIcons';
import { Colors, Font, Gap, Radius, goldFill } from '../theme';

type Props = {
  songId: string;
  accountId: string;
  playerName: string;
  defaultInstrument?: InstrumentKey;
  /** Pre-fetched history entries. When provided, skips internal fetch. */
  history?: ScoreHistoryEntry[];
  /** When provided, only these instruments are shown. */
  visibleInstruments?: InstrumentKey[];
};

type ChartPoint = {
  date: string;
  dateLabel: string;
  timestamp: number;
  score: number;
  accuracy: number;
  isFullCombo: boolean;
  stars?: number;
  season?: number;
};

// Simple in-memory cache so navigating between songs doesn't re-fetch
// Keyed by "accountId:songId"
const historyCache = new Map<string, ScoreHistoryEntry[]>();

export default function ScoreHistoryChart({
  songId,
  accountId,
  playerName,
  defaultInstrument,
  history: historyProp,
  visibleInstruments: visibleInstrumentsProp,
}: Props) {
  const cacheKey = `${accountId}:${songId}`;
  const [selected, setSelected] = useState<InstrumentKey>(defaultInstrument ?? 'Solo_Guitar');
  const [songHistory, setSongHistory] = useState<ScoreHistoryEntry[]>(
    () => historyProp ?? historyCache.get(cacheKey) ?? [],
  );
  const [loading, setLoading] = useState(!historyProp && !historyCache.has(cacheKey));

  useEffect(() => {
    // If pre-fetched data was provided, use it directly
    if (historyProp) {
      setSongHistory(historyProp);
      historyCache.set(cacheKey, historyProp);
      setLoading(false);
      return;
    }
    if (historyCache.has(cacheKey)) {
      setSongHistory(historyCache.get(cacheKey)!);
      setLoading(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    api
      .getPlayerHistory(accountId, songId)
      .then((res) => {
        if (!cancelled) {
          historyCache.set(cacheKey, res.history);
          setSongHistory(res.history);
        }
      })
      .catch(() => {
        if (!cancelled) setSongHistory([]);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [accountId, songId, cacheKey, historyProp]);

  const filtered = useMemo(
    () =>
      songHistory.filter(
        (h) => h.instrument === selected,
      ),
    [songHistory, selected],
  );

  const chartData: ChartPoint[] = useMemo(() => {
    const sorted = [...filtered].sort(
      (a, b) =>
        new Date(a.scoreAchievedAt ?? a.changedAt).getTime() -
        new Date(b.scoreAchievedAt ?? b.changedAt).getTime(),
    );
    // Build concise date labels: "Jun 5", "Jul 11", etc.
    // When multiple entries share a day, append index: "Jul 11 (2)"
    const daySeen = new Map<string, number>();
    const dayTotal = new Map<string, number>();
    for (const h of sorted) {
      const d = new Date(h.scoreAchievedAt ?? h.changedAt);
      const dayKey = d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
      dayTotal.set(dayKey, (dayTotal.get(dayKey) ?? 0) + 1);
    }
    return sorted.map((h) => {
      const d = new Date(h.scoreAchievedAt ?? h.changedAt);
      const dayKey = d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
      const total = dayTotal.get(dayKey) ?? 1;
      const idx = (daySeen.get(dayKey) ?? 0) + 1;
      daySeen.set(dayKey, idx);
      const dateLabel = total > 1 ? `${dayKey} (${idx})` : dayKey;
      return {
        date: d.toISOString(),
        dateLabel,
        timestamp: d.getTime(),
        score: h.newScore,
        accuracy: h.accuracy != null ? h.accuracy / 10000 : 0,
        isFullCombo: h.isFullCombo ?? false,
        stars: h.stars,
        season: h.season,
      };
    });
  }, [filtered]);

  // Count per-instrument entries so we can show which instruments have data
  const instrumentCounts = useMemo(() => {
    const counts: Partial<Record<InstrumentKey, number>> = {};
    for (const h of songHistory) {
      const key = h.instrument as InstrumentKey;
      counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
  }, [songHistory]);

  const instrumentPool = visibleInstrumentsProp ?? INSTRUMENT_KEYS;

  // Auto-select: prefer Lead, then first instrument with data, if current has none
  useEffect(() => {
    if ((instrumentCounts[selected] ?? 0) === 0 || !instrumentPool.includes(selected)) {
      const lead = instrumentPool.find(k => k === 'Solo_Guitar' && (instrumentCounts[k] ?? 0) > 0);
      if (lead) {
        setSelected(lead);
      } else {
        const first = instrumentPool.find((k) => (instrumentCounts[k] ?? 0) > 0);
        if (first) setSelected(first);
      }
    }
  }, [instrumentCounts, selected, instrumentPool]);

  const availableInstruments = useMemo(
    () => instrumentPool.filter((k) => (instrumentCounts[k] ?? 0) > 0),
    [instrumentCounts, instrumentPool],
  );

  // Measure container width to decide between full icon row vs compact arrows
  const iconRowRef = useRef<HTMLDivElement>(null);
  const [compact, setCompact] = useState(false);
  useEffect(() => {
    const el = iconRowRef.current;
    if (!el) return;
    const ro = new ResizeObserver(([entry]) => {
      const width = entry.contentRect.width;
      // Each button is 64px + gap (10px). Need room for all icons.
      const needed = availableInstruments.length * 64 + (availableInstruments.length - 1) * 10;
      setCompact(width < needed);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [availableInstruments.length]);

  const cycleInstrument = useCallback((dir: 1 | -1) => {
    const idx = availableInstruments.indexOf(selected);
    const next = (idx + dir + availableInstruments.length) % availableInstruments.length;
    setSelected(availableInstruments[next]);
  }, [availableInstruments, selected]);

  return (
    <div style={styles.wrapper}>
      {/* Chart area */}
      <div style={styles.chartContainer}>
        {/* Instrument icons */}
        {availableInstruments.length > 1 && (
          <div ref={iconRowRef} style={styles.iconRow}>
            {compact ? (
              <>
                <button
                  onClick={() => cycleInstrument(-1)}
                  style={styles.arrowButton}
                  aria-label="Previous instrument"
                >
                  <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M10 3L5 8L10 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
                </button>
                <button
                  style={{ ...styles.iconButton, ...styles.iconButtonActive }}
                >
                  <InstrumentIcon instrument={selected} size={48} />
                </button>
                <button
                  onClick={() => cycleInstrument(1)}
                  style={styles.arrowButton}
                  aria-label="Next instrument"
                >
                  <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M6 3L11 8L6 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
                </button>
              </>
            ) : (
              availableInstruments.map((inst) => {
                const isActive = inst === selected;
                return (
                  <button
                    key={inst}
                    onClick={() => setSelected(inst)}
                    style={{
                      ...styles.iconButton,
                      ...(isActive ? styles.iconButtonActive : {}),
                    }}
                  >
                    <InstrumentIcon instrument={inst} size={48} />
                  </button>
                );
              })
            )}
          </div>
        )}
        {loading && (
          <div style={styles.placeholder}>Loading history…</div>
        )}
        {!loading && chartData.length === 0 && (
          <div style={styles.placeholder}>
            No history for {INSTRUMENT_LABELS[selected]}
          </div>
        )}
        {!loading && chartData.length > 0 && (
          <ResponsiveContainer
            width="100%"
            height={320}
          >
            <ComposedChart
              data={chartData}
              margin={{ top: 16, right: 16, bottom: 16, left: 16 }}
            >
              <CartesianGrid
                strokeDasharray="3 3"
                stroke={Colors.borderSubtle}
              />
              <XAxis
                dataKey="dateLabel"
                tick={{ fill: Colors.textMuted, fontSize: Font.xs, dy: 8 }}
                stroke={Colors.borderSubtle}
                angle={-35}
                textAnchor="end"
                interval="preserveStartEnd"
              />
              <YAxis
                yAxisId="score"
                tick={{ fill: Colors.textMuted, fontSize: Font.xs }}
                stroke={Colors.borderSubtle}
                tickFormatter={(v: number) =>
                  v >= 1000 ? `${(v / 1000).toFixed(0)}k` : String(v)
                }
                label={{ value: 'Score', angle: -90, position: 'insideLeft', offset: 10, fill: Colors.textMuted, fontSize: Font.sm }}
              />
              <YAxis
                yAxisId="accuracy"
                orientation="right"
                domain={[0, 100]}
                tick={{ fill: Colors.textMuted, fontSize: Font.xs }}
                stroke={Colors.borderSubtle}
                tickFormatter={(v: number) => `${v}%`}
                label={{ value: 'Accuracy', angle: 90, position: 'insideRight', offset: 10, fill: Colors.textMuted, fontSize: Font.sm }}
              />
              <Tooltip content={<CustomTooltip />} />
              <Legend
                content={() => (
                  <div style={styles.legend}>
                    <span style={styles.legendItem}>
                      <span style={styles.legendGradient} />
                      Accuracy
                    </span>
                    <span style={styles.legendItem}>
                      <span style={styles.legendGold} />
                      Accuracy (FC)
                    </span>
                    <span style={styles.legendItem}>
                      <svg width={24} height={12} style={{ verticalAlign: 'middle' }}>
                        <line x1={0} y1={6} x2={18} y2={6} stroke={Colors.accentBlueBright} strokeWidth={2} />
                        <circle cx={18} cy={6} r={3} fill={Colors.accentBlueBright} />
                      </svg>
                      Score
                    </span>
                  </div>
                )}
              />
              <Bar
                yAxisId="accuracy"
                dataKey="accuracy"
                name="Accuracy"
                radius={[4, 4, 0, 0]}
                shape={(props: Record<string, unknown>) => {
                  const point = props as { x: number; y: number; width: number; height: number; payload: ChartPoint };
                  const acc = point.payload.accuracy;
                  const isGold = acc >= 100 && point.payload.isFullCombo;
                  let fill: string;
                  let fillOp: number;
                  let strokeColor: string;
                  let strokeOp: number;
                  if (isGold) {
                    fill = Colors.gold;
                    fillOp = 1;
                    strokeColor = Colors.gold;
                    strokeOp = 0.9;
                  } else {
                    // Linear red→green based on accuracy (0–100%)
                    const t = Math.min(Math.max(acc / 100, 0), 1);
                    const r = Math.round(220 * (1 - t) + 46 * t);
                    const g = Math.round(40 * (1 - t) + 204 * t);
                    const b = Math.round(40 * (1 - t) + 113 * t);
                    fill = `rgb(${r},${g},${b})`;
                    fillOp = 1;
                    strokeColor = fill;
                    strokeOp = 0.9;
                  }
                  const rad = 4;
                  const { x, y, width: w, height: h } = point;
                  const path = `M${x},${y + h} L${x},${y + rad} Q${x},${y} ${x + rad},${y} L${x + w - rad},${y} Q${x + w},${y} ${x + w},${y + rad} L${x + w},${y + h} Z`;
                  return (
                    <path
                      d={path}
                      fill={fill}
                      fillOpacity={fillOp}
                      stroke={strokeColor}
                      strokeOpacity={strokeOp}
                      strokeWidth={1}
                    />
                  );
                }}
              />
              <Line
                yAxisId="score"
                type="monotone"
                dataKey="score"
                name="Score"
                stroke={Colors.accentBlueBright}
                strokeWidth={2}
                dot={{ fill: Colors.accentBlueBright, r: 4 }}
                activeDot={{ r: 6, fill: Colors.accentBlue }}
              />
            </ComposedChart>
          </ResponsiveContainer>
        )}
      </div>
    </div>
  );
}

function CustomTooltip({
  active,
  payload,
}: {
  active?: boolean;
  payload?: Array<{ payload: ChartPoint }>;
}) {
  if (!active || !payload?.length) return null;
  const first = payload[0];
  if (!first) return null;
  const d = first.payload;
  return (
    <div style={styles.tooltip}>
      <div style={styles.tooltipDate}>
        {new Date(d.date).toLocaleDateString('en-US', {
          month: 'long',
          day: 'numeric',
          year: 'numeric',
        })}
        {' '}
        {new Date(d.date).toLocaleTimeString('en-US', {
          hour: 'numeric',
          minute: '2-digit',
        })}
        {d.season != null && (
          <span style={styles.tooltipSeason}> · S{d.season}</span>
        )}
      </div>
      <div style={styles.tooltipRow}>
        <span style={{ color: Colors.accentBlueBright, fontWeight: 600 }}>
          Score:
        </span>{' '}
        {d.score.toLocaleString()}
      </div>
      <div style={styles.tooltipRow}>
        <span style={{ color: Colors.accentPurple, fontWeight: 600 }}>
          Accuracy:
        </span>{' '}
        {d.accuracy % 1 === 0 ? `${d.accuracy}%` : `${d.accuracy.toFixed(1)}%`}
        {d.isFullCombo && (
          <span style={styles.tooltipFc}>FC</span>
        )}
      </div>
      {d.stars != null && (
        <div style={styles.tooltipRow}>
          {'★'.repeat(d.stars)}
        </div>
      )}
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  wrapper: {
    marginTop: Gap.section,
    marginBottom: Gap.section,
  },
  iconRow: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    gap: Gap.lg,
    paddingTop: Gap.md,
    paddingBottom: Gap.xs,
    width: '100%',
  },
  iconButton: {
    background: 'none',
    border: 'none',
    borderRadius: '50%',
    width: 64,
    height: 64,
    padding: 0,
    cursor: 'pointer',
    transition: 'all 0.15s ease',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    opacity: 0.5,
  },
  iconButtonActive: {
    backgroundColor: '#2ECC71',
    opacity: 1,
  },
  arrowButton: {
    background: 'none',
    border: `1px solid ${Colors.borderPrimary}`,
    borderRadius: '50%',
    width: 40,
    height: 40,
    padding: 0,
    cursor: 'pointer',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: Colors.textSecondary,
    fontSize: 24,
    fontWeight: 700,
    lineHeight: 1,
    transition: 'all 0.15s ease',
  },
  chartContainer: {
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(20px)',
    WebkitBackdropFilter: 'blur(20px)',
    border: `1px solid ${Colors.glassBorder}`,
    borderRadius: Radius.lg,
    padding: `${Gap.sm}px ${Gap.xl}px`,
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
  },
  placeholder: {
    color: Colors.textMuted,
    fontSize: Font.md,
    fontStyle: 'italic',
    textAlign: 'center' as const,
    padding: `${Gap.section}px 0`,
    width: '100%',
  },
  tooltip: {
    backgroundColor: Colors.surfaceFrosted,
    border: `1px solid ${Colors.borderPrimary}`,
    borderRadius: Radius.xs,
    padding: Gap.xl,
    backdropFilter: 'blur(12px)',
  },
  tooltipDate: {
    fontSize: Font.sm,
    fontWeight: 600,
    color: Colors.textPrimary,
    marginBottom: Gap.sm,
  },
  tooltipSeason: {
    color: Colors.textMuted,
    fontWeight: 400,
  },
  tooltipRow: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
    marginBottom: Gap.xs,
  },
  tooltipFc: {
    ...goldFill,
    marginLeft: Gap.md,
    fontSize: Font.xs,
    fontWeight: 700,
    padding: `0 ${Gap.sm}px`,
    borderRadius: Radius.xs,
  },
  legend: {
    display: 'flex',
    justifyContent: 'center',
    gap: Gap.xl,
    fontSize: Font.sm,
    color: Colors.textSecondary,
    paddingTop: 16,
  },
  legendItem: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: Gap.sm,
  },
  legendGradient: {
    display: 'inline-block',
    width: 20,
    height: 12,
    borderRadius: 2,
    background: 'linear-gradient(to right, rgb(220,40,40), rgb(46,204,113))',
  },
  legendGold: {
    display: 'inline-block',
    width: 20,
    height: 12,
    borderRadius: 2,
    backgroundColor: Colors.gold,
  },
};
