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
import { Colors, Font, Gap, Radius, goldFill, goldOutlineSkew, frostedCard } from '../theme';
import { useIsMobile } from '../hooks/useIsMobile';
import SeasonPill from './SeasonPill';

type Props = {
  songId: string;
  accountId: string;
  playerName: string;
  defaultInstrument?: InstrumentKey;
  /** Pre-fetched history entries. When provided, skips internal fetch. */
  history?: ScoreHistoryEntry[];
  /** When provided, only these instruments are shown. */
  visibleInstruments?: InstrumentKey[];
  /** When true, skip chart entry animations (e.g. returning from cache). */
  skipAnimation?: boolean;
  /** Fixed score column width (e.g. "7ch") for alignment with leaderboard cards. */
  scoreWidth?: string;
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

function accuracyColor(pct: number): string {
  const t = Math.min(Math.max(pct / 100, 0), 1);
  const r = Math.round(220 * (1 - t) + 46 * t);
  const g = Math.round(40 * (1 - t) + 204 * t);
  const b = Math.round(40 * (1 - t) + 113 * t);
  return `rgb(${r},${g},${b})`;
}

export default function ScoreHistoryChart({
  songId,
  accountId,
  playerName,
  defaultInstrument,
  history: historyProp,
  visibleInstruments: visibleInstrumentsProp,
  skipAnimation,
  scoreWidth: scoreWidthProp,
}: Props) {
  const cacheKey = `${accountId}:${songId}`;
  const isMobile = useIsMobile();
  const [selected, setSelected] = useState<InstrumentKey>(defaultInstrument ?? 'Solo_Guitar');
  const [selectedPoint, setSelectedPoint] = useState<ChartPoint | null>(null);
  const [displayedPoint, setDisplayedPoint] = useState<ChartPoint | null>(null);
  // Phase: 'closed' → 'growing' → 'open' → 'fading' → 'shrinking' → 'closed'
  // Swap: 'open' → 'swapOut' → 'swapIn' → 'open'
  const [cardPhase, setCardPhase] = useState<'closed' | 'growing' | 'open' | 'fading' | 'shrinking' | 'swapOut' | 'swapIn'>('closed');
  const cardPhaseRef = useRef(cardPhase);
  cardPhaseRef.current = cardPhase;
  const cardContentRef = useRef<HTMLDivElement>(null);
  const [cardHeight, setCardHeight] = useState(0);
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
    // Build concise date labels: "m/d/yy"
    // When multiple entries share a day, append index: "3/1/26 (2)"
    const daySeen = new Map<string, number>();
    const dayTotal = new Map<string, number>();
    const formatDay = (d: Date) => {
      const m = d.getMonth() + 1;
      const day = d.getDate();
      const yy = String(d.getFullYear()).slice(-2);
      return `${m}/${day}/${yy}`;
    };
    for (const h of sorted) {
      const d = new Date(h.scoreAchievedAt ?? h.changedAt);
      const dayKey = formatDay(d);
      dayTotal.set(dayKey, (dayTotal.get(dayKey) ?? 0) + 1);
    }
    return sorted.map((h) => {
      const d = new Date(h.scoreAchievedAt ?? h.changedAt);
      const dayKey = formatDay(d);
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

  // Measure chart container width to determine how many bars fit.
  //
  // Strategy: use the container div width from ResizeObserver as our monotonic
  // input. On first Recharts render, read the actual SVG clipPath rect to learn
  // the true axes overhead (container width − clip width). That overhead is then
  // locked and reused so bar count is a pure function of container width.
  const MIN_BAR_WIDTH = 96;
  const BAR_GAP = 8;
  const chartContainerRef = useRef<HTMLDivElement>(null);
  const [containerWidth, setContainerWidth] = useState(0);
  const axesOverheadRef = useRef<number | null>(null);

  useEffect(() => {
    const el = chartContainerRef.current;
    if (!el) return;
    const ro = new ResizeObserver(([entry]) => {
      const w = entry.contentRect.width;
      setContainerWidth(w);
      // Re-learn overhead if we don't have it yet
      if (axesOverheadRef.current === null) {
        const clip = el.querySelector('.recharts-surface clipPath rect');
        if (clip) {
          const clipW = parseFloat(clip.getAttribute('width') || '0');
          if (clipW > 0) {
            axesOverheadRef.current = w - clipW;
          }
        }
      }
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // After initial Recharts render, learn the overhead from the clip rect
  // This runs each render cycle until we capture a value.
  useEffect(() => {
    if (axesOverheadRef.current !== null || containerWidth === 0) return;
    // Use rAF to wait for Recharts to paint
    const raf = requestAnimationFrame(() => {
      const el = chartContainerRef.current;
      if (!el) return;
      const clip = el.querySelector('.recharts-surface clipPath rect');
      if (clip) {
        const clipW = parseFloat(clip.getAttribute('width') || '0');
        if (clipW > 0) {
          axesOverheadRef.current = containerWidth - clipW;
          // Force a re-render so maxBars recalculates with real overhead
          setContainerWidth((prev) => prev);
        }
      }
    });
    return () => cancelAnimationFrame(raf);
  });

  const FALLBACK_OVERHEAD = 188; // reasonable default until we measure
  const overhead = axesOverheadRef.current ?? FALLBACK_OVERHEAD;
  const plotWidth = Math.max(0, containerWidth - overhead);
  // Before the first ResizeObserver measurement (containerWidth === 0), show all
  // data to avoid a flash. Once measured, bar count is monotonic with container width.
  const maxBars = containerWidth === 0
    ? chartData.length
    : Math.max(1, Math.floor((plotWidth + BAR_GAP) / (MIN_BAR_WIDTH + BAR_GAP)));

  const [chartPage, setChartPage] = useState(0); // 0 = last page (most recent)

  // Reset page when instrument changes
  useEffect(() => { setChartPage(0); }, [selected]);

  const totalPages = Math.max(1, Math.ceil(chartData.length / maxBars));
  // Page 0 = most recent (last slice), page N = oldest.
  // Slice from the END so the most-recent page is always full and only the
  // oldest page (highest chartPage) can have fewer than maxBars entries.
  const pageEnd = chartData.length - chartPage * maxBars;
  const pageStart = Math.max(0, pageEnd - maxBars);
  const visibleChartData = chartData.slice(pageStart, pageEnd);

  // Sequenced card animation: grow → fade-in, fade-out → shrink
  // When swapping: swapOut old content → swapIn new content (card stays open)
  const cardTimers = useRef<ReturnType<typeof setTimeout>[]>([]);
  const pendingPoint = useRef<ChartPoint | null>(null);
  const displayedPointRef = useRef(displayedPoint);
  displayedPointRef.current = displayedPoint;
  useEffect(() => {
    cardTimers.current.forEach(clearTimeout);
    cardTimers.current = [];
    const phase = cardPhaseRef.current;
    const shown = displayedPointRef.current;
    if (selectedPoint) {
      // Swap: card already open, switching to different point
      if (shown && (phase === 'open' || phase === 'swapIn' || phase === 'swapOut')) {
        pendingPoint.current = selectedPoint;
        setCardPhase('swapOut');
        cardTimers.current.push(setTimeout(() => {
          setDisplayedPoint(pendingPoint.current);
          pendingPoint.current = null;
          setCardPhase('swapIn');
          cardTimers.current.push(setTimeout(() => setCardPhase('open'), 150));
        }, 150));
      } else {
        // Opening from closed
        setDisplayedPoint(selectedPoint);
        requestAnimationFrame(() => {
          if (cardContentRef.current) {
            setCardHeight(cardContentRef.current.offsetHeight + 2);
          }
          setCardPhase('growing');
          cardTimers.current.push(setTimeout(() => setCardPhase('open'), 250));
        });
      }
    } else if (shown && phase !== 'closed') {
      setCardPhase('fading');
      cardTimers.current.push(setTimeout(() => {
        setCardPhase('shrinking');
        cardTimers.current.push(setTimeout(() => {
          setDisplayedPoint(null);
          setCardPhase('closed');
        }, 250));
      }, 200));
    }
    return () => { cardTimers.current.forEach(clearTimeout); };
  }, [selectedPoint]); // eslint-disable-line react-hooks/exhaustive-deps

  // Clear selected score card when instrument changes
  useEffect(() => { setSelectedPoint(null); }, [selected]);

  // Animated score card list beneath chart
  const visibleCards = useMemo(() => chartData.slice(-5), [chartData]);
  const [displayedCards, setDisplayedCards] = useState<ChartPoint[]>(visibleCards);
  const [listPhase, setListPhase] = useState<'idle' | 'out' | 'in'>('idle');
  const [listHeight, setListHeight] = useState(() => {
    const n = visibleCards.length;
    return n > 0 ? n * 48 + (n - 1) * 4 : 0; // card height + gap
  });
  const listHeightRef = useRef(listHeight);
  listHeightRef.current = listHeight;
  const listTimers = useRef<ReturnType<typeof setTimeout>[]>([]);
  const prevCardsRef = useRef(visibleCards);

  useEffect(() => {
    // Skip animation on first render or if cards are the same
    if (prevCardsRef.current === visibleCards) return;
    prevCardsRef.current = visibleCards;

    listTimers.current.forEach(clearTimeout);
    listTimers.current = [];

    const oldCount = displayedCards.length;
    const outDuration = oldCount > 0 ? 200 + (oldCount - 1) * 40 : 0;

    if (oldCount > 0) {
      const newN = visibleCards.length;
      const newHeight = newN > 0 ? newN * 48 + (newN - 1) * 4 : 0;
      const isShrinking = newHeight < listHeightRef.current;

      setListPhase('out');
      listTimers.current.push(setTimeout(() => {
        if (isShrinking) {
          // Shrink: animate height down first, then swap content
          setListHeight(newHeight);
          listTimers.current.push(setTimeout(() => {
            setDisplayedCards(visibleCards);
            setListPhase('in');
            const inDuration = 300 + (newN - 1) * 60;
            listTimers.current.push(setTimeout(() => setListPhase('idle'), inDuration));
          }, 300));
        } else {
          // Grow: clear old cards, animate height up, then show new cards
          setDisplayedCards([]);
          // Use rAF to ensure the empty state renders before height change
          requestAnimationFrame(() => {
            setListHeight(newHeight);
            listTimers.current.push(setTimeout(() => {
              setDisplayedCards(visibleCards);
              setListPhase('in');
              const inDuration = 300 + (newN - 1) * 60;
              listTimers.current.push(setTimeout(() => setListPhase('idle'), inDuration));
            }, 300));
          });
        }
      }, outDuration));
    } else {
      setDisplayedCards(visibleCards);
      const newN = visibleCards.length;
      setListHeight(newN > 0 ? newN * 48 + (newN - 1) * 4 : 0);
      setListPhase('in');
      const newCount = visibleCards.length;
      const inDuration = 300 + (newCount - 1) * 60;
      listTimers.current.push(setTimeout(() => setListPhase('idle'), inDuration));
    }

    return () => { listTimers.current.forEach(clearTimeout); };
  }, [visibleCards]); // eslint-disable-line react-hooks/exhaustive-deps

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
      <div style={styles.chartContainer} ref={chartContainerRef}>
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
        {isMobile && (
          <div style={styles.chartHeader}>
            <div style={styles.chartTitle}>Score History</div>
            <div style={styles.chartSubtitle}>Select a bar to see more score details.</div>
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
              data={visibleChartData}
              margin={{ top: 16, right: 24, bottom: 0, left: 24 }}
              barCategoryGap="10%"
            >
              <CartesianGrid
                strokeDasharray="3 3"
                stroke={Colors.borderSubtle}
                horizontal={false}
                vertical={false}
              />
              <XAxis
                dataKey="dateLabel"
                tick={{ fill: '#fff', fontSize: Font.md, dy: 16 }}
                stroke={Colors.borderSubtle}
                axisLine={false}
                tickLine={false}
                angle={-35}
                textAnchor="end"
                interval="preserveStartEnd"
              />
              <YAxis
                yAxisId="score"
                tick={{ fill: '#fff', fontSize: Font.md }}
                stroke={Colors.borderSubtle}
                axisLine={false}
                tickLine={false}
                tickFormatter={(v: number) =>
                  v >= 1000 ? `${(v / 1000).toFixed(0)}k` : String(v)
                }
                label={({ viewBox }: { viewBox: { x: number; y: number; height: number } }) => {
                  const cy = viewBox.y + viewBox.height / 2;
                  return (
                    <text x={viewBox.x - 8} y={cy} fill="#fff" fontSize={Font.md} textAnchor="middle" dominantBaseline="central" transform={`rotate(-90, ${viewBox.x - 8}, ${cy})`}>Score</text>
                  );
                }}
              />
              <YAxis
                yAxisId="accuracy"
                orientation="right"
                domain={[0, 100]}
                padding={{ top: 4 }}
                tick={{ fill: '#fff', fontSize: Font.md }}
                stroke={Colors.borderSubtle}
                axisLine={false}
                tickLine={false}
                tickFormatter={(v: number) => `${v}%`}
                label={({ viewBox }: { viewBox: { x: number; y: number; width: number; height: number } }) => {
                  const cy = viewBox.y + viewBox.height / 2;
                  const lx = viewBox.x + viewBox.width + 8;
                  return (
                    <text x={lx} y={cy} fill="#fff" fontSize={Font.md} textAnchor="middle" dominantBaseline="central" transform={`rotate(90, ${lx}, ${cy})`}>Accuracy</text>
                  );
                }}
              />
              {!isMobile && <Tooltip content={<CustomTooltip />} />}
              {isMobile && <Tooltip content={() => null} cursor={{ fill: 'transparent', stroke: 'transparent' }} trigger="click" />}
              <Legend
                content={() => {
                  const hasFc = visibleChartData.some(p => p.accuracy >= 100 && p.isFullCombo);
                  const hasNonFc = visibleChartData.some(p => !(p.accuracy >= 100 && p.isFullCombo));
                  return (
                  <div style={styles.legend}>
                    {hasNonFc && (
                    <span style={styles.legendItem}>
                      <span style={styles.legendGradient} />
                      Accuracy
                    </span>
                    )}
                    {hasFc && (
                    <span style={styles.legendItem}>
                      <span style={styles.legendGold} />
                      Accuracy (FC)
                    </span>
                    )}
                    <span style={styles.legendItem}>
                      <svg width={24} height={12} style={{ verticalAlign: 'middle' }}>
                        <line x1={0} y1={6} x2={18} y2={6} stroke={Colors.accentBlueBright} strokeWidth={2} />
                        <circle cx={18} cy={6} r={3} fill={Colors.accentBlueBright} />
                      </svg>
                      Score
                    </span>
                  </div>
                  );
                }}
              />
              <Bar
                yAxisId="accuracy"
                dataKey="accuracy"
                name="Accuracy"
                radius={[4, 4, 0, 0]}
                isAnimationActive={!skipAnimation}
                onClick={isMobile ? (_data: Record<string, unknown>, index: number) => {
                  const point = visibleChartData[index];
                  setSelectedPoint(prev => prev === point ? null : point);
                } : undefined}
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
                      stroke="none"
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
                activeDot={isMobile ? false : { r: 6, fill: Colors.accentBlue }}
                isAnimationActive={!skipAnimation}
              />
            </ComposedChart>
          </ResponsiveContainer>
        )}
        {isMobile && (
          <div style={{
            overflow: 'hidden',
            maxHeight: (cardPhase === 'growing' || cardPhase === 'open' || cardPhase === 'fading' || cardPhase === 'swapOut' || cardPhase === 'swapIn') ? cardHeight : 0,
            transition: `max-height 0.25s ${cardPhase === 'shrinking' ? 'ease-in' : 'ease-out'}`,
            marginTop: cardPhase !== 'closed' ? Gap.xl : 0,
            alignSelf: 'stretch',
          }}>
            {displayedPoint && (
              <div style={{
                ...styles.scoreCard,
                opacity: (cardPhase === 'open' || cardPhase === 'swapOut' || cardPhase === 'swapIn') ? 1 : 0,
                transform: (cardPhase === 'open' || cardPhase === 'swapOut' || cardPhase === 'swapIn') ? 'translateY(0)' : 'translateY(-8px)',
                transition: 'opacity 0.15s ease, transform 0.15s ease',
              }} ref={cardContentRef}>
                <div style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: Gap.xl,
                  width: '100%',
                  opacity: (cardPhase === 'open' || cardPhase === 'swapIn') ? 1 : (cardPhase === 'swapOut' ? 0 : undefined),
                  transform: (cardPhase === 'open' || cardPhase === 'swapIn') ? 'translateY(0)' : (cardPhase === 'swapOut' ? 'translateY(-6px)' : undefined),
                  transition: (cardPhase === 'swapOut' || cardPhase === 'swapIn') ? 'opacity 0.12s ease, transform 0.12s ease' : 'none',
                }}>
                  <span style={styles.scoreCardDate}>
                    {new Date(displayedPoint.date).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
                  </span>
                  <span style={styles.scoreCardMiddle}>
                    {displayedPoint.season != null && (
                      <SeasonPill season={displayedPoint.season} />
                    )}
                    <span style={{ ...styles.scoreCardScore, width: scoreWidthProp }}>
                      {displayedPoint.score.toLocaleString()}
                    </span>
                  </span>
                  <span style={styles.scoreCardAcc}>
                    {(() => {
                      const pct = displayedPoint.accuracy;
                      const text = pct % 1 === 0 ? `${pct}%` : `${pct.toFixed(1)}%`;
                      return displayedPoint.isFullCombo
                        ? <span style={styles.fcAccBadge}>{text}</span>
                        : <span style={{ color: accuracyColor(pct) }}>{text}</span>;
                    })()}
                  </span>
                </div>
              </div>
            )}
          </div>
        )}
      </div>
      {/* Player score cards beneath chart */}
      {isMobile && (displayedCards.length > 0 || listHeight > 0) && (
        <div style={{
          overflow: 'hidden',
          height: listHeight,
          transition: 'height 0.3s ease',
          marginTop: Gap.xl,
        }}>
          <div style={styles.scoreCardList}>
          {displayedCards.map((point, i) => {
            const pct = point.accuracy;
            const text = pct % 1 === 0 ? `${pct}%` : `${pct.toFixed(1)}%`;
            const count = displayedCards.length;

            let animStyle: React.CSSProperties = {};
            if (listPhase === 'out') {
              // Stagger out top-down: first card fades first
              animStyle = {
                opacity: 0,
                transform: 'translateY(-8px)',
                transition: `opacity 0.15s ease-in ${i * 40}ms, transform 0.15s ease-in ${i * 40}ms`,
              };
            } else if (listPhase === 'in') {
              // Stagger in top-down: matches leaderboard card animation
              animStyle = {
                opacity: 0,
                animation: `fadeInUp 300ms ease-out ${i * 60}ms forwards`,
              };
            }

            return (
              <div
                key={point.date}
                style={{
                  ...styles.scoreListCard,
                  ...animStyle,
                }}
              >
                <span style={styles.scoreCardDate}>
                  {new Date(point.date).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
                </span>
                <span style={styles.scoreCardMiddle}>
                  {point.season != null && (
                    <SeasonPill season={point.season} />
                  )}
                  <span style={{ ...styles.scoreCardScore, width: scoreWidthProp }}>
                    {point.score.toLocaleString()}
                  </span>
                </span>
                <span style={styles.scoreCardAcc}>
                  {point.isFullCombo
                    ? <span style={styles.fcAccBadge}>{text}</span>
                    : <span style={{ color: accuracyColor(pct) }}>{text}</span>
                  }
                </span>
              </div>
            );
          })}
        </div>
        </div>
      )}
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
    ...frostedCard,
    borderRadius: Radius.lg,
    padding: `${Gap.sm}px ${Gap.xl}px 0`,
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
  },
  tooltipDate: {
    fontSize: Font.sm,
    fontWeight: 600,
    color: '#fff',
    marginBottom: Gap.sm,
  },
  tooltipSeason: {
    color: Colors.textMuted,
    fontWeight: 400,
  },
  tooltipRow: {
    fontSize: Font.sm,
    color: '#fff',
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
    fontSize: Font.md,
    color: '#fff',
    paddingTop: 36,
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
  chartHeader: {
    textAlign: 'center' as const,
    marginBottom: Gap.md,
  },
  chartTitle: {
    color: '#fff',
    fontSize: Font.title,
    fontWeight: 700,
  },
  chartSubtitle: {
    color: Colors.textMuted,
    fontSize: Font.lg,
    marginTop: Gap.xs,
  },
  scoreCard: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `0 0`,
    height: 48,
    fontSize: Font.md,
    color: 'inherit',
    width: '100%',
    boxSizing: 'border-box' as const,
  },
  scoreCardDate: {
    flex: 1,
    minWidth: 0,
    color: Colors.textPrimary,
    fontSize: Font.md,
  },
  scoreCardMiddle: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.sm,
    flexShrink: 0,
  },
  scoreCardScore: {
    flexShrink: 0,
    textAlign: 'right' as const,
    fontWeight: 600,
    color: Colors.textPrimary,
    fontVariantNumeric: 'tabular-nums',
  },
  scoreCardAcc: {
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
  scoreCardList: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.sm,
  },
  scoreListCard: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `0 ${Gap.xl}px`,
    height: 48,
    borderRadius: Radius.md,
    ...frostedCard,
    fontSize: Font.md,
    color: 'inherit',
    transition: 'border-color 0.15s',
  },
  scoreListCardActive: {
    borderColor: Colors.accentBlueBright,
  },
};
