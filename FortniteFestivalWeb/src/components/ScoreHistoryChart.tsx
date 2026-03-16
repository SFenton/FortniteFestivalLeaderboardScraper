import { useEffect, useState, useMemo, useRef, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
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
import {
  INSTRUMENT_KEYS,
  INSTRUMENT_LABELS,
  type InstrumentKey,
  type ScoreHistoryEntry,
} from '../models';
import { accuracyColor } from '@festival/core';
import { InstrumentIcon } from './InstrumentIcons';
import { Colors, Font, Gap, Radius, goldFill, goldOutlineSkew, frostedCard } from '@festival/theme';
import { useIsMobile } from '../hooks/useIsMobile';
import SeasonPill from './SeasonPill';
import { useChartData, type ChartPoint } from './chart/useChartData';
import ChartTooltip from './chart/ChartTooltip';

type Props = {
  songId: string;
  accountId: string;
  playerName: string;
  defaultInstrument?: InstrumentKey;
  history?: ScoreHistoryEntry[];
  visibleInstruments?: InstrumentKey[];
  skipAnimation?: boolean;
  scoreWidth?: string;
};

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
  const navigate = useNavigate();
  const isMobile = useIsMobile();
  const [selected, setSelected] = useState<InstrumentKey>(defaultInstrument ?? 'Solo_Guitar');
  const [selectedPoint, setSelectedPoint] = useState<ChartPoint | null>(null);
  const [displayedPoint, setDisplayedPoint] = useState<ChartPoint | null>(null);
  const [cardPhase, setCardPhase] = useState<'closed' | 'growing' | 'open' | 'fading' | 'shrinking' | 'swapOut' | 'swapIn'>('closed');
  const cardPhaseRef = useRef(cardPhase);
  cardPhaseRef.current = cardPhase;
  const cardContentRef = useRef<HTMLDivElement>(null);
  const [cardHeight, setCardHeight] = useState(0);

  const { songHistory, chartData, loading, instrumentCounts } = useChartData(accountId, songId, selected, historyProp);

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

  // Offset-based pagination: 0 = most recent entries visible.
  // Increase offset to scroll back in time, decrease to scroll forward.
  const [chartOffset, setChartOffset] = useState(0);

  // Reset offset when instrument changes
  useEffect(() => { setChartOffset(0); }, [selected]);

  const maxOffset = Math.max(0, chartData.length - maxBars);
  const clampedOffset = Math.min(chartOffset, maxOffset);
  const pageEnd = chartData.length - clampedOffset;
  const pageStart = Math.max(0, pageEnd - maxBars);
  const visibleChartData = chartData.slice(pageStart, pageEnd);
  const needsPagination = chartData.length > maxBars;

  // Index of the selected point within the full chartData array
  const selectedIndex = useMemo(() => {
    if (!selectedPoint) return -1;
    return chartData.findIndex(p => p.date === selectedPoint.date && p.score === selectedPoint.score);
  }, [selectedPoint, chartData]);

  // Navigation helpers: when a point is selected, step through chartData
  // and adjust the offset so the target point is visible.
  const navigatePoint = useCallback((targetIdx: number) => {
    const clamped = Math.max(0, Math.min(targetIdx, chartData.length - 1));
    const point = chartData[clamped];
    setSelectedPoint(point);
    // Ensure the target index is within the visible window.
    // visible window covers indices [pageStart, pageEnd) where
    //   pageEnd = chartData.length - offset
    //   pageStart = pageEnd - maxBars
    // We need: pageStart <= clamped < pageEnd
    setChartOffset(prev => {
      const curEnd = chartData.length - Math.min(prev, maxOffset);
      const curStart = Math.max(0, curEnd - maxBars);
      if (clamped >= curStart && clamped < curEnd) return prev; // already visible
      // Shift so target is at the edge in the direction we're moving
      if (clamped < curStart) {
        // Moving backward: put target at the start of the window
        return Math.min(chartData.length - clamped - maxBars, maxOffset);
      }
      // Moving forward: put target at the end of the window
      return Math.max(chartData.length - clamped - 1, 0);
    });
  }, [chartData, maxBars, maxOffset]);

  // Button disabled states depend on whether a point is selected
  const backDisabled = selectedPoint
    ? selectedIndex <= 0
    : clampedOffset >= maxOffset;
  const forwardDisabled = selectedPoint
    ? selectedIndex >= chartData.length - 1
    : clampedOffset <= 0;

  // Enable Recharts animation when paginating so bars/line animate naturally
  const [animatingPage, setAnimatingPage] = useState(false);
  const pageAnimTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const paginateChart = useCallback((action: () => void, willPageChange: boolean, _direction: 'back' | 'forward') => {
    if (!willPageChange) {
      action();
      return;
    }
    if (pageAnimTimer.current) clearTimeout(pageAnimTimer.current);
    setAnimatingPage(true);
    action();
    pageAnimTimer.current = setTimeout(() => setAnimatingPage(false), 600);
  }, []);

  const chartAnimActive = animatingPage;

  // Check if a target chartData index is within the current visible window
  const isOnCurrentPage = useCallback((idx: number) => {
    return idx >= pageStart && idx < pageEnd;
  }, [pageStart, pageEnd]);

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
  const visibleCards = useMemo(() => [...chartData].sort((a, b) => b.score - a.score).slice(0, 5), [chartData]);
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

      // Skip animation when restoring from cache — just swap the data silently
      if (skipAnimation) {
        setDisplayedCards(visibleCards);
        setListHeight(newHeight);
        setListPhase('idle');
        return;
      }

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
      if (skipAnimation) {
        setListPhase('idle');
      } else {
        setListPhase('in');
        const newCount = visibleCards.length;
        const inDuration = 300 + (newCount - 1) * 60;
        listTimers.current.push(setTimeout(() => setListPhase('idle'), inDuration));
      }
    }

    return () => { listTimers.current.forEach(clearTimeout); };
  }, [visibleCards]); // eslint-disable-line react-hooks/exhaustive-deps

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
        <div style={styles.chartHeader}>
          <div style={styles.chartTitle}>Score History</div>
          <div style={styles.chartSubtitle}>Select a bar to see more score details.</div>
        </div>
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
              <Tooltip content={() => null} cursor={{ fill: 'transparent', stroke: 'transparent' }} trigger="click" />
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
                isAnimationActive={chartAnimActive}
                animationDuration={400}
                onClick={(_data: Record<string, unknown>, index: number) => {
                  const point = visibleChartData[index];
                  setSelectedPoint(prev => prev === point ? null : point);
                }}
                shape={(props: Record<string, unknown>) => {
                  const point = props as { x: number; y: number; width: number; height: number; payload: ChartPoint };
                  const acc = point.payload.colorAccuracy ?? point.payload.accuracy;
                  const isGold = acc >= 100 && point.payload.isFullCombo;
                  const isSelected = selectedPoint != null
                    && point.payload.date === selectedPoint.date
                    && point.payload.score === selectedPoint.score;
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
                  const path = `M${x + rad},${y + h} Q${x},${y + h} ${x},${y + h - rad} L${x},${y + rad} Q${x},${y} ${x + rad},${y} L${x + w - rad},${y} Q${x + w},${y} ${x + w},${y + rad} L${x + w},${y + h - rad} Q${x + w},${y + h} ${x + w - rad},${y + h} Z`;
                  return (
                    <path
                      d={path}
                      fill={fill}
                      fillOpacity={fillOp}
                      stroke={isSelected ? Colors.accentPurple : 'none'}
                      strokeWidth={isSelected ? 3 : 0}
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
                isAnimationActive={chartAnimActive}
                animationDuration={400}
              />
            </ComposedChart>
          </ResponsiveContainer>
        )}
        {displayedPoint && (
          <div style={{
            overflow: 'hidden',
            maxHeight: (cardPhase === 'growing' || cardPhase === 'open' || cardPhase === 'fading' || cardPhase === 'swapOut' || cardPhase === 'swapIn') ? cardHeight : 0,
            transition: `max-height 0.25s ${cardPhase === 'shrinking' ? 'ease-in' : 'ease-out'}`,
            marginTop: Gap.xl,
            alignSelf: 'stretch',
            ...(!isMobile ? { width: '50%', marginLeft: 'auto', marginRight: 'auto' } : {}),
          }}>
            {displayedPoint && (
              <div style={{
                ...styles.scoreCard,
                ...(!isMobile ? styles.scoreListCard : {}),
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
        {/* Chart pagination controls */}
        {!loading && needsPagination && (
          <div style={styles.chartPagination}>
            <button
              style={{
                ...styles.chartPageButton,
                ...(backDisabled ? styles.chartPageButtonDisabled : {}),
              }}
              disabled={backDisabled}
              onClick={() => {
                const target = selectedIndex - maxBars;
                const willChange = selectedPoint ? !isOnCurrentPage(Math.max(0, target)) : true;
                paginateChart(() => {
                  if (selectedPoint) {
                    navigatePoint(target);
                  } else {
                    setChartOffset(o => Math.min(o + maxBars, maxOffset));
                  }
                }, willChange, 'back');
              }}
              aria-label="Back one page"
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M9 3L4 8L9 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/><path d="M14 3L9 8L14 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              style={{
                ...styles.chartPageButton,
                ...(backDisabled ? styles.chartPageButtonDisabled : {}),
              }}
              disabled={backDisabled}
              onClick={() => {
                const target = selectedIndex - 1;
                const willChange = selectedPoint ? !isOnCurrentPage(target) : true;
                paginateChart(() => {
                  if (selectedPoint) {
                    navigatePoint(target);
                  } else {
                    setChartOffset(o => Math.min(o + 1, maxOffset));
                  }
                }, willChange, 'back');
              }}
              aria-label="Back one entry"
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M10 3L5 8L10 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              style={{
                ...styles.chartPageButton,
                ...(forwardDisabled ? styles.chartPageButtonDisabled : {}),
                marginLeft: Gap.md,
              }}
              disabled={forwardDisabled}
              onClick={() => {
                const target = selectedIndex + 1;
                const willChange = selectedPoint ? !isOnCurrentPage(target) : true;
                paginateChart(() => {
                  if (selectedPoint) {
                    navigatePoint(target);
                  } else {
                    setChartOffset(o => Math.max(o - 1, 0));
                  }
                }, willChange, 'forward');
              }}
              aria-label="Forward one entry"
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M6 3L11 8L6 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              style={{
                ...styles.chartPageButton,
                ...(forwardDisabled ? styles.chartPageButtonDisabled : {}),
              }}
              disabled={forwardDisabled}
              onClick={() => {
                const target = selectedIndex + maxBars;
                const willChange = selectedPoint ? !isOnCurrentPage(Math.min(target, chartData.length - 1)) : true;
                paginateChart(() => {
                  if (selectedPoint) {
                    navigatePoint(target);
                  } else {
                    setChartOffset(o => Math.max(o - maxBars, 0));
                  }
                }, willChange, 'forward');
              }}
              aria-label="Forward one page"
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M7 3L12 8L7 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/><path d="M2 3L7 8L2 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
          </div>
        )}
      </div>
      {/* Player score cards beneath chart */}
      {(displayedCards.length > 0 || listHeight > 0) && (
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
      {chartData.length > 5 && (
        <button style={styles.viewAllButton} onClick={() => navigate(`/songs/${songId}/${selected}/history`)}>
          View all available player scores
        </button>
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
    padding: `${Gap.sm}px ${Gap.xl}px ${Gap.xl}px`,
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
    fontSize: Font.md,
    color: Colors.textPrimary,
    fontVariantNumeric: 'tabular-nums',
  },
  scoreCardAcc: {
    width: 60,
    flexShrink: 0,
    textAlign: 'center' as const,
    fontWeight: 600,
    fontSize: Font.md,
    color: Colors.accentBlueBright,
    fontVariantNumeric: 'tabular-nums',
  },
  fcAccBadge: {
    ...goldOutlineSkew,
    fontSize: Font.md,
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
  chartPagination: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    gap: Gap.md,
    paddingTop: Gap.xl,
    paddingBottom: Gap.md,
  },
  chartPageButton: {
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
    transition: 'all 0.15s ease',
  },
  chartPageButtonDisabled: {
    opacity: 0.3,
    cursor: 'default',
  },
  viewAllButton: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '100%',
    height: 48,
    marginTop: Gap.sm,
    borderRadius: Radius.md,
    ...frostedCard,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: 600,
    cursor: 'pointer',
    transition: 'background-color 0.15s',
  },
};
