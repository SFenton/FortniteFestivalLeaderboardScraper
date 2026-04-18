/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, useEffect, useState, useMemo, useCallback } from 'react';
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
import { ACCURACY_SCALE } from '@festival/core';
import GraphCard from '../../../../components/common/GraphCard';
import { LeaderboardEntry } from '../../../leaderboard/global/components/LeaderboardEntry';
import { Colors, Font, Gap, Size, Layout, Radius, CHART_ANIM_DURATION, frostedCard, padding, border, transition, QUERY_SHOW_SEASON, QUERY_SHOW_ACCURACY } from '@festival/theme';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useMediaQuery } from '../../../../hooks/ui/useMediaQuery';
import { useChartData, type ChartPoint } from '../../../../hooks/chart/useChartData';

/* ── Chart visual constants ── */

/** Shared tick style for all axes. */
const AXIS_TICK = { fill: Colors.textPrimary, fontSize: Font.md };

/** X-axis tick style (extra downward offset for rotated labels). */
const X_AXIS_TICK = { ...AXIS_TICK, dy: 16 };

/** X-axis label rotation angle. */
const X_AXIS_ANGLE = -35;

/** Identity function for matching ChartPoints across pagination. */
const CHART_POINT_IDENTITY = (a: ChartPoint, b: ChartPoint) =>
  a.date === b.date && a.score === b.score;

/* ── Score card list styles ── */
const scoreListCardBase: React.CSSProperties = {
  ...frostedCard, display: 'flex', alignItems: 'center', gap: Gap.xl,
  padding: padding(0, Gap.xl), height: Size.iconXl, borderRadius: Radius.md,
  fontSize: Font.md, color: 'inherit', transition: transition('border-color', 150),
};
const scoreListCardBestStyle: React.CSSProperties = {
  ...scoreListCardBase,
  backgroundColor: Colors.purpleHighlight,
  border: border(1, Colors.purpleHighlightBorder),
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
  sig?: string;
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
  sig,
}: ScoreHistoryChartProps) {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const showSeason = useMediaQuery(QUERY_SHOW_SEASON);
  const showAccuracy = useMediaQuery(QUERY_SHOW_ACCURACY);
  const st = useChartStyles();
  const [selected, setSelected] = useState<InstrumentKey>(defaultInstrument ?? DEFAULT_INSTRUMENT);

  const { songHistory: _songHistory, chartData, loading, instrumentCounts } = useChartData(accountId, songId, selected, historyProp);

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

  /* v8 ignore start — InstrumentSelector always provides non-null key */
  const handleInstrumentSelect = useCallback((key: InstrumentKey) => {
    setSelected(key);
  }, []);
  /* v8 ignore stop */

  // Top 5 scores for the list beneath the chart
  const visibleCards = useMemo(() => [...chartData].sort((a, b) => b.score - a.score).slice(0, 5), [chartData]);

  const handleViewAll = useCallback(() => {
    /* v8 ignore next — navigation */
    navigate(`/songs/${songId}/${selected}/history`);
  }, [navigate, songId, selected]);

  const renderChart = useCallback(({ visibleData, animating, selectedPoint, setSelectedPoint }: {
    visibleData: ChartPoint[];
    animating: boolean;
    selectedPoint: ChartPoint | null;
    setSelectedPoint: (p: ChartPoint | null | ((prev: ChartPoint | null) => ChartPoint | null)) => void;
  }) => (
    <ResponsiveContainer width="100%" height={Size.chartHeight}>
      <ComposedChart
        data={visibleData}
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
            const hasFc = visibleData.some(p => p.accuracy >= 100 && p.isFullCombo);
            const hasNonFc = visibleData.some(p => !(p.accuracy >= 100 && p.isFullCombo));
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
          isAnimationActive={animating}
          animationDuration={CHART_ANIM_DURATION}
          onClick={(_data: Record<string, unknown>, index: number) => {
            const point = visibleData[index];
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
          isAnimationActive={animating}
          animationDuration={CHART_ANIM_DURATION}
        />
      </ComposedChart>
    </ResponsiveContainer>
  ), [t, st, isMobile]);

  const renderDetailCard = useCallback((point: ChartPoint) => (
    <LeaderboardEntry
      label={new Date(point.date).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
      displayName=""
      score={point.score}
      season={point.season}
      accuracy={point.accuracy * ACCURACY_SCALE}
      isFullCombo={!!point.isFullCombo}
      difficulty={point.difficulty}
      showDifficulty={point.difficulty != null}
      showSeason={point.season != null}
      showAccuracy
      scoreWidth={scoreWidthProp}
    />
  ), [scoreWidthProp]);

  const renderListItem = useCallback((point: ChartPoint, i: number, phase: 'idle' | 'in' | 'out') => {
    let animStyle: React.CSSProperties = {};
    /* v8 ignore start — list animation styles */
    if (phase === 'out') {
      animStyle = {
        opacity: 0,
        transform: 'translateY(-8px)',
        transition: `opacity 0.15s ease-in ${i * 40}ms, transform 0.15s ease-in ${i * 40}ms`,
      };
    } else if (phase === 'in') {
      animStyle = {
        opacity: 0,
        animation: `fadeInUp 300ms ease-out ${i * 60}ms forwards`,
      };
    }
    /* v8 ignore stop */
    return (
      <div key={point.date} style={{ ...(i === 0 ? scoreListCardBestStyle : scoreListCardBase), ...animStyle }}>
        <LeaderboardEntry
          label={new Date(point.date).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
          displayName=""
          score={point.score}
          season={point.season}
          accuracy={point.accuracy * ACCURACY_SCALE}
          isFullCombo={!!point.isFullCombo}
          isPlayer={i === 0}
          difficulty={point.difficulty}
          showDifficulty={showSeason}
          showSeason={showSeason}
          showAccuracy={showAccuracy}
          scoreWidth={scoreWidthProp}
        />
      </div>
    );
  }, [showSeason, showAccuracy, scoreWidthProp]);

  return (
    <GraphCard<ChartPoint>
      data={chartData}
      loading={loading}
      instruments={selectorItems}
      selected={selected}
      onInstrumentSelect={handleInstrumentSelect}
      sig={sig}
      title={t('chart.scoreHistory')}
      subtitle={t('chart.selectBarHint')}
      loadingMessage={t('chart.loadingHistory')}
      emptyMessage={t('chart.noHistory', { instrument: instrumentLabel(selected) })}
      identity={CHART_POINT_IDENTITY}
      renderChart={renderChart}
      renderDetailCard={renderDetailCard}
      listData={visibleCards}
      renderListItem={renderListItem}
      viewAllLabel={chartData.length > 5 ? t('chart.viewAllScores') : undefined}
      onViewAll={chartData.length > 5 ? handleViewAll : undefined}
      skipAnimation={skipAnimation}
    />
  );
});

function useChartStyles() {
  return useMemo(() => ({
    legend: { display: 'flex', justifyContent: 'center', gap: Gap.xl, fontSize: Font.md, color: Colors.textPrimary, paddingTop: 36 } as React.CSSProperties,
    legendItem: { display: 'inline-flex', alignItems: 'center', gap: Gap.sm } as React.CSSProperties,
    legendGradient: { display: 'inline-block', width: Size.iconXs, height: 12, borderRadius: 2, background: 'linear-gradient(to right, rgb(220,40,40), rgb(46,204,113))' } as React.CSSProperties,
    legendGold: { display: 'inline-block', width: Size.iconXs, height: 12, borderRadius: 2, backgroundColor: Colors.gold } as React.CSSProperties,
  }), []);
}

