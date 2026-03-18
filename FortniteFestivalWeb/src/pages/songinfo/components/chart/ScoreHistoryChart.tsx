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
import { SERVER_INSTRUMENT_KEYS as INSTRUMENT_KEYS, type ServerInstrumentKey as InstrumentKey, serverInstrumentLabel as instrumentLabel, type ServerScoreHistoryEntry as ScoreHistoryEntry } from '@festival/core/api/serverTypes';
import { CardPhase, ACCURACY_SCALE } from '@festival/core';
import { InstrumentSelector } from '../../../../components/common/InstrumentSelector';
import AccuracyDisplay from '../../../../components/songs/metadata/AccuracyDisplay';
import { Colors, Font, Gap } from '@festival/theme';
import s from './ScoreHistoryChart.module.css';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import SeasonPill from '../../../../components/songs/metadata/SeasonPill';
import { useChartData, type ChartPoint } from '../../../../hooks/chart/useChartData';
import { useChartDimensions } from '../../../../hooks/chart/useChartDimensions';
import { useChartPagination } from '../../../../hooks/chart/useChartPagination';
import { useCardAnimation } from '../../../../hooks/chart/useCardAnimation';
import { useListAnimation } from '../../../../hooks/chart/useListAnimation';
import ScoreCardList from './ScoreCardList';

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
  const [selected, setSelected] = useState<InstrumentKey>(defaultInstrument ?? 'Solo_Guitar');

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
    pageAnimTimer.current = setTimeout(() => setAnimatingPage(false), 600);
  }, []);
  /* v8 ignore stop */

  const chartAnimActive = animatingPage;

  // Check if a target chartData index is within the current visible window
  const isOnCurrentPage = useCallback((idx: number) => {
    return idx >= pageStart && idx < pageEnd;
  }, [pageStart, pageEnd]);

  // Card animation (selected point detail card)
  const { displayedPoint, cardPhase, cardHeight, cardContentRef } = useCardAnimation(selectedPoint);

  // Animated score card list beneath chart
  const visibleCards = useMemo(() => [...chartData].sort((a, b) => b.score - a.score).slice(0, 5), [chartData]);
  const { displayedCards, listPhase, listHeight } = useListAnimation(visibleCards, skipAnimation);

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

  const selectorItems = useMemo(
    () => availableInstruments.map(key => ({ key })),
    [availableInstruments],
  );

  const selectorClassNames = useMemo(() => ({
    row: s.iconRow,
    button: s.iconButton,
    buttonActive: s.iconButtonActive,
    arrowButton: s.arrowButton,
  }), []);

  const compactLabels = useMemo(() => ({
    previous: t('aria.previousInstrument'),
    next: t('aria.nextInstrument'),
  }), [t]);

  const handleInstrumentSelect = useCallback((key: InstrumentKey | null) => {
    if (key) setSelected(key);
  }, []);

  // Measure container width to decide between full icon row vs compact arrows
  const iconRowRef = useRef<HTMLDivElement>(null);
  const [compact, setCompact] = useState(false);
  useEffect(() => {
    const el = iconRowRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;
      const width = entry.contentRect.width;
      // Each button is 64px + gap (10px). Need room for all icons.
      const needed = availableInstruments.length * 64 + (availableInstruments.length - 1) * 10;
      setCompact(width < needed);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [availableInstruments.length]);

  return (
    <div className={s.wrapper}>
      {/* Chart area */}
      <div className={s.chartContainer} ref={chartContainerRef}>
        {/* Instrument icons */}
        {availableInstruments.length > 1 && (
          <div ref={iconRowRef}>
            <InstrumentSelector
              instruments={selectorItems}
              selected={selected}
              onSelect={handleInstrumentSelect}
              required
              compact={compact}
              compactLabels={compactLabels}
              classNames={selectorClassNames}
            />
          </div>
        )}
        <div className={s.chartHeader}>
          <div className={s.chartTitle}>{t('chart.scoreHistory')}</div>
          <div className={s.chartSubtitle}>{t('chart.selectBarHint')}</div>
        </div>
        {loading && (
          <div className={s.placeholder}>{t('chart.loadingHistory')}</div>
        )}
        {!loading && chartData.length === 0 && (
          <div className={s.placeholder}>
            {t('chart.noHistory', {instrument: instrumentLabel(selected)})}
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
                    <text x={viewBox.x - 8} y={cy} fill="#fff" fontSize={Font.md} textAnchor="middle" dominantBaseline="central" transform={`rotate(-90, ${viewBox.x - 8}, ${cy})`}>{t('chart.score')}</text>
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
                    <text x={lx} y={cy} fill="#fff" fontSize={Font.md} textAnchor="middle" dominantBaseline="central" transform={`rotate(90, ${lx}, ${cy})`}>{t('chart.accuracy')}</text>
                  );
                }}
              />
              <Tooltip content={() => null} cursor={{ fill: 'transparent', stroke: 'transparent' }} trigger="click" />
              <Legend
                content={() => {
                  const hasFc = visibleChartData.some(p => p.accuracy >= 100 && p.isFullCombo);
                  const hasNonFc = visibleChartData.some(p => !(p.accuracy >= 100 && p.isFullCombo));
                  return (
                  <div className={s.legend}>
                    {hasNonFc && (
                    <span className={s.legendItem}>
                      <span className={s.legendGradient} />
                      {t('chart.accuracy')}
                    </span>
                    )}
                    {hasFc && (
                    <span className={s.legendItem}>
                      <span className={s.legendGold} />
                      {t('chart.accuracyFC')}
                    </span>
                    )}
                    <span className={s.legendItem}>
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
              {/* @ts-expect-error Recharts Bar shape/onClick types are overly strict */}
              <Bar
                yAxisId="accuracy"
                dataKey="accuracy"
                name={t('chart.accuracy')}
                radius={[4, 4, 0, 0]}
                isAnimationActive={chartAnimActive}
                animationDuration={400}
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
                    // Linear red→green based on accuracy (0–100%)
                    const t = Math.min(Math.max(acc / 100, 0), 1);
                    const r = Math.round(220 * (1 - t) + 46 * t);
                    const g = Math.round(40 * (1 - t) + 204 * t);
                    const b = Math.round(40 * (1 - t) + 113 * t);
                    fill = `rgb(${r},${g},${b})`;
                    fillOp = 1;
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
                name={t('chart.score')}
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
            maxHeight: (cardPhase === CardPhase.Growing || cardPhase === CardPhase.Open || cardPhase === CardPhase.Fading || cardPhase === CardPhase.SwapOut || cardPhase === CardPhase.SwapIn) ? cardHeight : 0,
            transition: `max-height 0.25s ${cardPhase === CardPhase.Shrinking ? 'ease-in' : 'ease-out'}`,
            marginTop: Gap.xl,
            alignSelf: 'stretch',
            ...(!isMobile ? { width: '50%', marginLeft: 'auto', marginRight: 'auto' } : {}),
          }}>
            {displayedPoint && (
              <div className={`${s.scoreCard}${!isMobile ? ` ${s.scoreListCard}` : ''}`} style={{
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
                  <span className={s.scoreCardDate}>
                    {new Date(displayedPoint.date).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
                  </span>
                  <span className={s.scoreCardMiddle}>
                    {displayedPoint.season != null && (
                      <SeasonPill season={displayedPoint.season} />
                    )}
                    <span className={s.scoreCardScore} style={{ width: scoreWidthProp }}>
                      {displayedPoint.score.toLocaleString()}
                    </span>
                  </span>
                  <span className={s.scoreCardAcc}>
                    <AccuracyDisplay
                      accuracy={displayedPoint.accuracy * ACCURACY_SCALE}
                      isFullCombo={!!displayedPoint.isFullCombo}
                    />
                  </span>
                </div>
              </div>
            )}
          </div>
        )}
        {/* Chart pagination controls */}
        {/* v8 ignore start — pagination animation callbacks */}
        {!loading && needsPagination && (
          <div className={s.chartPagination}>
            <button
              className={backDisabled ? s.chartPageButtonDisabled : s.chartPageButton} style={{
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
              aria-label={t('aria.backOnePage')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M9 3L4 8L9 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/><path d="M14 3L9 8L14 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              className={backDisabled ? s.chartPageButtonDisabled : s.chartPageButton} style={{
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
              aria-label={t('aria.backOneEntry')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M10 3L5 8L10 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              className={forwardDisabled ? s.chartPageButtonDisabled : s.chartPageButton} style={{
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
              aria-label={t('aria.forwardOneEntry')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M6 3L11 8L6 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
            <button
              className={forwardDisabled ? s.chartPageButtonDisabled : s.chartPageButton} style={{
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
              aria-label={t('aria.forwardOnePage')}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M7 3L12 8L7 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/><path d="M2 3L7 8L2 13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
            </button>
          </div>
        )}
        {/* v8 ignore stop */}
      </div>
      <ScoreCardList
        displayedCards={displayedCards}
        listHeight={listHeight}
        listPhase={listPhase}
        scoreWidth={scoreWidthProp}
      />
      {chartData.length > 5 && (
        <button className={s.viewAllButton} onClick={() => navigate(`/songs/${songId}/${selected}/history`)}>
          {t('chart.viewAllScores')}
        </button>
      )}
    </div>
  );
});

