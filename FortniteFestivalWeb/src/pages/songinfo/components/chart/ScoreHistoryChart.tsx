/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, useEffect, useState, useMemo, useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
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
import { SERVER_INSTRUMENT_KEYS as INSTRUMENT_KEYS, type ServerInstrumentKey as InstrumentKey, serverInstrumentLabel as instrumentLabel, type ServerScoreHistoryEntry as ScoreHistoryEntry, DEFAULT_INSTRUMENT } from '@festival/core/api/serverTypes';
import { CardPhase, ACCURACY_SCALE } from '@festival/core';
import { InstrumentSelector } from '../../../../components/common/InstrumentSelector';
import { LeaderboardEntry } from '../../../leaderboard/global/components/LeaderboardEntry';
import { Colors, Font, Gap, Size, Layout, Radius, Weight, CHART_ANIM_DURATION, CHART_ANIM_SETTLE, frostedCard, padding, border, transition, flexCenter, Cursor, Position, Overflow, Opacity, Display, Align, Justify, CssValue } from '@festival/theme';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useChartData, type ChartPoint } from '../../../../hooks/chart/useChartData';
import { useChartDimensions } from '../../../../hooks/chart/useChartDimensions';
import { useChartPagination } from '../../../../hooks/chart/useChartPagination';
import { useCardAnimation } from '../../../../hooks/chart/useCardAnimation';
import { useListAnimation } from '../../../../hooks/chart/useListAnimation';
import ScoreCardList from './ScoreCardList';

/* ── Chart visual constants ── */

/** Shared tick style for all axes. */
const AXIS_TICK = { fill: Colors.textPrimary, fontSize: Font.md };

/** X-axis tick style (extra downward offset for rotated labels). */
const X_AXIS_TICK = { ...AXIS_TICK, dy: 16 };

/** X-axis label rotation angle. */
const X_AXIS_ANGLE = -35;

/* ── Inline style constants (migrated from CSS module) ── */
const FAST_TRANSITION = 'all 0.15s ease';
const selectorStyles = (() => {
  const circleBtn: React.CSSProperties = {
    background: CssValue.none, border: border(1, Colors.borderPrimary), borderRadius: CssValue.circle,
    width: Size.iconLg, height: Size.iconLg, padding: 0, cursor: Cursor.pointer,
    ...flexCenter, color: Colors.textSecondary, transition: FAST_TRANSITION,
  };
  const selectorIconBtn: React.CSSProperties = {
    background: CssValue.none, border: CssValue.none, borderRadius: CssValue.circle,
    width: Layout.demoInstrumentBtn, height: Layout.demoInstrumentBtn,
    padding: 0, cursor: Cursor.pointer, transition: FAST_TRANSITION,
    ...flexCenter, opacity: Opacity.disabled,
    position: Position.relative, overflow: Overflow.hidden,
  };
  return {
    row: { display: Display.flex, justifyContent: Justify.center, alignItems: Align.center, gap: Gap.lg, width: CssValue.full } as React.CSSProperties,
    button: selectorIconBtn,
    buttonActive: { ...selectorIconBtn, backgroundColor: Colors.statusGreen, opacity: 1 } as React.CSSProperties,
    arrowButton: { ...circleBtn } as React.CSSProperties,
  };
})();

const chartCircleBtn: React.CSSProperties = {
  background: CssValue.none, border: border(1, Colors.borderPrimary), borderRadius: CssValue.circle,
  width: Size.iconLg, height: Size.iconLg, padding: 0, cursor: Cursor.pointer,
  ...flexCenter, color: Colors.textSecondary, transition: FAST_TRANSITION,
};
type ScoreHistoryChartProps = {
  songId: string;
  accountId: string;
  playerName: string;
  defaultInstrument?: InstrumentKey;
  history?: ScoreHistoryEntry[];
  visibleInstruments?: InstrumentKey[];
  skipAnimation?: boolean;
  scoreWidth?: string;
};

export default memo(function ScoreHistoryChart({
  songId,
  accountId,
  playerName: _playerName,
  defaultInstrument,
  history: historyProp,
  visibleInstruments: visibleInstrumentsProp,
  skipAnimation,
  scoreWidth: scoreWidthProp,
}: ScoreHistoryChartProps) {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const st = useChartStyles();
  const [selected, setSelected] = useState<InstrumentKey>(defaultInstrument ?? DEFAULT_INSTRUMENT);

  const { songHistory: _songHistory, chartData, loading, instrumentCounts } = useChartData(accountId, songId, selected, historyProp);

  // Chart container sizing → bar count
  const chartContainerRef = useRef<HTMLDivElement>(null);
  const { maxBars } = useChartDimensions(chartContainerRef);

  // Pagination + point selection
  const {
    chartOffset: _chartOffset, setChartOffset, selectedPoint, setSelectedPoint,
    selectedIndex, visibleChartData, needsPagination,
    navigatePoint, backDisabled, forwardDisabled, maxOffset, clampedOffset: _clampedOffset,
    pageStart, pageEnd,
  } = useChartPagination(chartData, maxBars, selected);

  // Enable Recharts animation when paginating so bars/line animate naturally
  const [animatingPage, setAnimatingPage] = useState(false);
  const pageAnimTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  /* v8 ignore start — animation wrapper */
  const paginateChart = useCallback((action: () => void, willPageChange: boolean, _direction: 'back' | 'forward') => {
    if (!willPageChange) {
      action();
      return;
    }
    if (pageAnimTimer.current) clearTimeout(pageAnimTimer.current);
    setAnimatingPage(true);
    action();
    pageAnimTimer.current = setTimeout(() => setAnimatingPage(false), CHART_ANIM_SETTLE);
  }, []);
  /* v8 ignore stop */

  const chartAnimActive = animatingPage;

  // Check if a target chartData index is within the current visible window
  /* v8 ignore start — pagination check */
  const isOnCurrentPage = useCallback((idx: number) => {
    return idx >= pageStart && idx < pageEnd;
  }, [pageStart, pageEnd]);
  /* v8 ignore stop */

  // Card animation (selected point detail card)
  const { displayedPoint, cardPhase, cardHeight, cardContentRef } = useCardAnimation(selectedPoint);

  // Animated score card list beneath chart
  const visibleCards = useMemo(() => [...chartData].sort((a, b) => b.score - a.score).slice(0, 5), [chartData]);
  const { displayedCards, listPhase, listHeight } = useListAnimation(visibleCards, skipAnimation);

  const instrumentPool = visibleInstrumentsProp ?? INSTRUMENT_KEYS;

  // Auto-select: prefer Lead, then first instrument with data, if current has none
  useEffect(() => {
    /* v8 ignore start — instrument fallback logic */
    if ((instrumentCounts[selected] ?? 0) === 0 || !instrumentPool.includes(selected)) {
      const lead = instrumentPool.find(k => k === DEFAULT_INSTRUMENT && (instrumentCounts[k] ?? 0) > 0);
      if (lead) {
        setSelected(lead);
      } else {
        const first = instrumentPool.find((k) => (instrumentCounts[k] ?? 0) > 0);
        if (first) setSelected(first);
      }
    }
    /* v8 ignore stop */
  }, [instrumentCounts, selected, instrumentPool]);

  const availableInstruments = useMemo(
    () => instrumentPool.filter((k) => (instrumentCounts[k] ?? 0) > 0),
    [instrumentCounts, instrumentPool],
  );

  const selectorItems = useMemo(
    () => availableInstruments.map(key => ({ key })),
    [availableInstruments],
  );

  const selectorStyleOverrides = useMemo(() => selectorStyles, []);

  const compactLabels = useMemo(() => ({
    previous: t('aria.previousInstrument'),
    next: t('aria.nextInstrument'),
  }), [t]);

  /* v8 ignore start — InstrumentSelector always provides non-null key */
  const handleInstrumentSelect = useCallback((key: InstrumentKey | null) => {
    if (key) setSelected(key);
  }, []);
  /* v8 ignore stop */

  // Measure container width to decide between full icon row vs compact arrows
  /* v8 ignore start — ResizeObserver compact layout */
  const iconRowRef = useRef<HTMLDivElement>(null);
  const [compact, setCompact] = useState(false);
  useEffect(() => {
    const el = iconRowRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;
      const width = entry.contentRect.width;
      const buttonSize = 64; // matches --size-3xl (icon button width)
      const needed = availableInstruments.length * buttonSize + (availableInstruments.length - 1) * Gap.lg;
      setCompact(width < needed);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [availableInstruments.length]);
  /* v8 ignore stop */

  return (
    <div>
      {/* Chart area */}
      <div style={st.chartContainer} ref={chartContainerRef}>
        {/* Instrument icons */}
        {availableInstruments.length > 1 && (
          <div ref={iconRowRef} style={st.iconRowWrap}>
            <InstrumentSelector
              instruments={selectorItems}
              selected={selected}
              onSelect={handleInstrumentSelect}
              required
              compact={compact}
              compactLabels={compactLabels}
              styles={selectorStyleOverrides}
            />
          </div>
        )}
        <div style={st.chartHeader}>
          <div style={st.chartTitle}>{t('chart.scoreHistory')}</div>
          <div style={st.chartSubtitle}>{t('chart.selectBarHint')}</div>
        </div>
        {/* v8 ignore start — chart conditional rendering */}
      {loading && (
          <div style={st.placeholder}>{t('chart.loadingHistory')}</div>
        )}
        {!loading && chartData.length === 0 && (
          <div style={st.placeholder}>
            {t('chart.noHistory', {instrument: instrumentLabel(selected)})}
          </div>
        )}
        {!loading && chartData.length > 0 && (
          <ResponsiveContainer
            width="100%"
            height={Size.chartHeight}
          >
            <ComposedChart
              data={visibleChartData}
              margin={Layout.chartMargin}
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
                tick={X_AXIS_TICK}
                stroke={Colors.borderSubtle}
                angle={X_AXIS_ANGLE}
                textAnchor="end"
                interval="preserveStartEnd"
              />
              <YAxis
                yAxisId="score"
                tick={AXIS_TICK}
                stroke={Colors.borderSubtle}
                tickFormatter={(v: number) =>
                  v >= 1000 ? `${(v / 1000).toFixed(0)}k` : String(v)
                }
                label={({ viewBox }: { viewBox: { x: number; y: number; height: number } }) => {
                  const cy = viewBox.y + viewBox.height / 2;
                  return (
                    <text x={viewBox.x - Layout.axisLabelOffset} y={cy} fill={Colors.textPrimary} fontSize={Font.md} textAnchor="middle" dominantBaseline="central" transform={`rotate(-90, ${viewBox.x - Layout.axisLabelOffset}, ${cy})`}>{t('chart.score')}</text>
                  );
                }}
              />
              <YAxis
                yAxisId="accuracy"
                orientation="right"
                domain={[0, 100]}
                padding={{ top: 4 }}
                tick={AXIS_TICK}
                stroke={Colors.borderSubtle}
                tickFormatter={(v: number) => `${v}%`}
                label={({ viewBox }: { viewBox: { x: number; y: number; width: number; height: number } }) => {
                  const cy = viewBox.y + viewBox.height / 2;
                  const lx = viewBox.x + viewBox.width + Layout.axisLabelOffset;
                  return (
                    <text x={lx} y={cy} fill={Colors.textPrimary} fontSize={Font.md} textAnchor="middle" dominantBaseline="central" transform={`rotate(90, ${lx}, ${cy})`}>{t('chart.accuracy')}</text>
                  );
                }}
              />
              <Tooltip content={() => null} cursor={{ fill: 'transparent', stroke: 'transparent' }} trigger="click" />
              <Legend
                content={() => {
                  const hasFc = visibleChartData.some(p => p.accuracy >= 100 && p.isFullCombo);
                  const hasNonFc = visibleChartData.some(p => !(p.accuracy >= 100 && p.isFullCombo));
                  return (
                  <div style={st.legend}>
                    {hasNonFc && (
                    <span style={st.legendItem}>
                      <span style={st.legendGradient} />
                      {t('chart.accuracy')}
                    </span>
                    )}
                    {hasFc && (
                    <span style={st.legendItem}>
                      <span style={st.legendGold} />
                      {t('chart.accuracyFC')}
                    </span>
                    )}
                    <span style={st.legendItem}>
                      <svg width={24} height={12} style={{ verticalAlign: 'middle' }}>
                        <line x1={0} y1={6} x2={18} y2={6} stroke={Colors.accentBlueBright} strokeWidth={2} />
                        <circle cx={18} cy={6} r={3} fill={Colors.accentBlueBright} />
                      </svg>
                      {t('chart.score')}
                    </span>
                  </div>
                  );
                }}
              />
              {/* v8 ignore start — bar shape/click handlers */}
              {/* @ts-expect-error Recharts Bar shape/onClick types are overly strict */}
              <Bar
                yAxisId="accuracy"
                dataKey="accuracy"
                name={t('chart.accuracy')}
                radius={Radius.barCorner}
                isAnimationActive={chartAnimActive}
                animationDuration={CHART_ANIM_DURATION}
                onClick={(_data: Record<string, unknown>, index: number) => {
                  const point = visibleChartData[index];
                  if (!point) return;
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
                  if (isGold) {
                    fill = Colors.gold;
                    fillOp = 1;
                  } else {
                    const t = Math.min(Math.max(acc / 100, 0), 1);
                    const r = Math.round(Colors.accuracyLow.r * (1 - t) + Colors.accuracyHigh.r * t);
                    const g = Math.round(Colors.accuracyLow.g * (1 - t) + Colors.accuracyHigh.g * t);
                    const b = Math.round(Colors.accuracyLow.b * (1 - t) + Colors.accuracyHigh.b * t);
                    fill = `rgb(${r},${g},${b})`;
                    fillOp = 1;
                  }
                  const rad = Radius.barCorner[0];
                  const { x, y, width: w, height: h } = point;
                  const path = `M${x + rad},${y + h} Q${x},${y + h} ${x},${y + h - rad} L${x},${y + rad} Q${x},${y} ${x + rad},${y} L${x + w - rad},${y} Q${x + w},${y} ${x + w},${y + rad} L${x + w},${y + h - rad} Q${x + w},${y + h} ${x + w - rad},${y + h} Z`;
                  return (
                    <path
                      d={path}
                      style={{ transition: transition('stroke', 150) }}
                      fill={fill}
                      fillOpacity={fillOp}
                      stroke={isSelected ? Colors.accentPurple : 'transparent'}
                      strokeWidth={Size.barSelectionStroke}
                    />
                  );
                }}
              />
              {/* v8 ignore stop */}
              <Line
                yAxisId="score"
                type="monotone"
                dataKey="score"
                name={t('chart.score')}
                stroke={Colors.accentBlueBright}
                strokeWidth={2}
                dot={{ fill: Colors.accentBlueBright, r: Size.dotRadius }}
                activeDot={isMobile ? false : { r: Size.dotRadiusActive, fill: Colors.accentBlue }}
                isAnimationActive={chartAnimActive}
                animationDuration={CHART_ANIM_DURATION}
              />
            </ComposedChart>
          </ResponsiveContainer>
        )}
        {displayedPoint && (
          <div style={{
            overflow: 'hidden',
            maxHeight: (cardPhase === CardPhase.Growing || cardPhase === CardPhase.Open || cardPhase === CardPhase.Fading || cardPhase === CardPhase.SwapOut || cardPhase === CardPhase.SwapIn) ? cardHeight : 0,
            transition: `max-height 0.25s ${cardPhase === CardPhase.Shrinking ? 'ease-in' : 'ease-out'}`,
            marginTop: Gap.xl,
            alignSelf: 'stretch',
            ...(!isMobile ? { width: '50%', marginLeft: 'auto', marginRight: 'auto' } : {}),
          }}>
            {displayedPoint && (
              <div style={{
                ...st.scoreCard,
                ...(!isMobile ? st.scoreListCard : {}),
                opacity: (cardPhase === CardPhase.Open || cardPhase === CardPhase.SwapOut || cardPhase === CardPhase.SwapIn) ? 1 : 0,
                transform: (cardPhase === CardPhase.Open || cardPhase === CardPhase.SwapOut || cardPhase === CardPhase.SwapIn) ? 'translateY(0)' : 'translateY(-8px)',
                transition: 'opacity 0.15s ease, transform 0.15s ease',
              }} ref={cardContentRef}>
                <div style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: Gap.xl,
                  width: '100%',
                  opacity: (cardPhase === CardPhase.Open || cardPhase === CardPhase.SwapIn) ? 1 : (cardPhase === CardPhase.SwapOut ? 0 : undefined),
                  transform: (cardPhase === CardPhase.Open || cardPhase === CardPhase.SwapIn) ? 'translateY(0)' : (cardPhase === CardPhase.SwapOut ? 'translateY(-6px)' : undefined),
                  transition: (cardPhase === CardPhase.SwapOut || cardPhase === CardPhase.SwapIn) ? 'opacity 0.12s ease, transform 0.12s ease' : 'none',
                }}>
                  <LeaderboardEntry
                    label={new Date(displayedPoint.date).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
                    displayName=""
                    score={displayedPoint.score}
                    season={displayedPoint.season}
                    accuracy={displayedPoint.accuracy * ACCURACY_SCALE}
                    isFullCombo={!!displayedPoint.isFullCombo}
                    showSeason={displayedPoint.season != null}
                    showAccuracy
                    scoreWidth={scoreWidthProp}
                  />
                </div>
              </div>
            )}
          </div>
        )}
        {/* Chart pagination controls */}
        {/* v8 ignore start — pagination animation callbacks */}
        {!loading && needsPagination && (
          <div style={st.chartPagination}>
            <button
              style={backDisabled ? st.chartPageButtonDisabled : st.chartPageButton}
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
              aria-label={t('aria.backOnePage')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M9 3L4 8L9 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/><path d="M14 3L9 8L14 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              style={backDisabled ? st.chartPageButtonDisabled : st.chartPageButton}
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
              aria-label={t('aria.backOneEntry')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M10 3L5 8L10 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              style={forwardDisabled ? st.chartPageButtonDisabled : st.chartPageButton}
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
              aria-label={t('aria.forwardOneEntry')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M6 3L11 8L6 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              style={forwardDisabled ? st.chartPageButtonDisabled : st.chartPageButton}
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
              aria-label={t('aria.forwardOnePage')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M7 3L12 8L7 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/><path d="M2 3L7 8L2 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
          </div>
        )}
        {/* v8 ignore stop */}
      </div>
      {/* v8 ignore stop — end chart conditional rendering */}
      <ScoreCardList
        displayedCards={displayedCards}
        listHeight={listHeight}
        listPhase={listPhase}
        scoreWidth={scoreWidthProp}
      />
      {chartData.length > 5 && (
        <button style={st.viewAllButton} onClick={() => {
          /* v8 ignore next — navigation */
          navigate(`/songs/${songId}/${selected}/history`);
        }}>
          {t('chart.viewAllScores')}
        </button>
      )}
    </div>
  );
});

function useChartStyles() {
  return useMemo(() => ({
    iconRowWrap: { width: '100%' } as React.CSSProperties,
    chartContainer: {
      ...frostedCard, borderRadius: Radius.lg, padding: padding(Gap.sm, Gap.xl, Gap.xl),
      display: 'flex', flexDirection: 'column' as const, alignItems: 'center',
    } as React.CSSProperties,
    placeholder: { color: Colors.textMuted, fontSize: Font.md, fontStyle: 'italic', textAlign: 'center' as const, padding: padding(Gap.section, 0), width: '100%' } as React.CSSProperties,
    legend: { display: 'flex', justifyContent: 'center', gap: Gap.xl, fontSize: Font.md, color: Colors.textPrimary, paddingTop: 36 } as React.CSSProperties,
    legendItem: { display: 'inline-flex', alignItems: 'center', gap: Gap.sm } as React.CSSProperties,
    legendGradient: { display: 'inline-block', width: Size.iconXs, height: 12, borderRadius: 2, background: 'linear-gradient(to right, rgb(220,40,40), rgb(46,204,113))' } as React.CSSProperties,
    legendGold: { display: 'inline-block', width: Size.iconXs, height: 12, borderRadius: 2, backgroundColor: Colors.gold } as React.CSSProperties,
    chartHeader: { textAlign: 'center' as const, marginBottom: Gap.md } as React.CSSProperties,
    chartTitle: { color: Colors.textPrimary, fontSize: Font.title, fontWeight: Weight.bold } as React.CSSProperties,
    chartSubtitle: { color: Colors.textMuted, fontSize: Font.lg, marginTop: Gap.xs } as React.CSSProperties,
    scoreCard: { display: 'flex', alignItems: 'center', gap: Gap.xl, height: Size.iconXl, fontSize: Font.md, color: 'inherit', width: '100%', boxSizing: 'border-box' as const } as React.CSSProperties,
    scoreListCard: {
      ...frostedCard, display: 'flex', alignItems: 'center', gap: Gap.xl,
      padding: padding(0, Gap.xl), height: Size.iconXl, borderRadius: Radius.md,
      fontSize: Font.md, color: 'inherit', transition: transition('border-color', 150),
    } as React.CSSProperties,
    chartPagination: { display: 'flex', justifyContent: 'center', alignItems: 'center', gap: Gap.md, paddingTop: Gap.xl, paddingBottom: Gap.md } as React.CSSProperties,
    chartPageButton: { ...chartCircleBtn } as React.CSSProperties,
    chartPageButtonDisabled: { ...chartCircleBtn, opacity: 0.3, cursor: 'default' } as React.CSSProperties,
    viewAllButton: {
      ...frostedCard, display: 'flex', alignItems: 'center', justifyContent: 'center',
      width: '100%', height: Size.iconXl, marginTop: Gap.sm, borderRadius: Radius.md,
      color: Colors.textPrimary, fontSize: Font.md, fontWeight: Weight.semibold,
      cursor: 'pointer', transition: transition('background-color', 150),
    } as React.CSSProperties,
  }), []);
}

