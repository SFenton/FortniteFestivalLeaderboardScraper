/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useState, useMemo, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
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
import type { ServerInstrumentKey as InstrumentKey, RankingMetric } from '@festival/core/api/serverTypes';
import { serverInstrumentLabel as instrumentLabel } from '@festival/core/api/serverTypes';
import { formatLeaderboardPercentile, formatRatingValue, rankColor } from '@festival/core';
import GraphCard from '../../../components/common/GraphCard';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';
import { useRankHistoryAll, formatValueTick, formatDetailValue, type RankHistoryChartPoint } from '../../../hooks/chart/useRankHistory';
import { parseSnapshotDate } from '../../../utils/fillRankHistoryGaps';
import { computeRankWidth } from '../helpers/rankingHelpers';
import {
  Colors, Font, FontVariant, Gap, Size, Layout, MetadataSize, Radius, Weight,
  frostedCard, padding, border, transition,
  CHART_ANIM_DURATION,
} from '@festival/theme';

/* ── Chart visual constants ── */
const AXIS_TICK = { fill: Colors.textPrimary, fontSize: Font.md };
const X_AXIS_TICK = { ...AXIS_TICK, dy: 16 };
const X_AXIS_ANGLE = -35;

/** Gradient endpoints matching accuracyColor scale for legend display. */
const RANK_GRADIENT = 'linear-gradient(to right, rgb(220,40,40), rgb(46,204,113))';

/** Identity function for matching RankHistoryChartPoints across pagination. */
const RANK_POINT_IDENTITY = (a: RankHistoryChartPoint, b: RankHistoryChartPoint) =>
  a.date === b.date && a.rank === b.rank && a.value === b.value;

const formatSnapshotDisplayDate = (snapshotDate: string) =>
  parseSnapshotDate(snapshotDate).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });

/* ── List card styles ── */
const listCardBase: React.CSSProperties = {
  ...frostedCard, display: 'flex', alignItems: 'center', gap: Gap.xl,
  padding: padding(0, Gap.xl), height: Size.iconXl, borderRadius: Radius.md,
  fontSize: Font.md, color: 'inherit', transition: transition('border-color', 150),
};
const listCardBest: React.CSSProperties = {
  ...listCardBase,
  backgroundColor: Colors.purpleHighlight,
  border: border(1, Colors.purpleHighlightBorder),
};

type RankHistoryChartProps = {
  accountId: string | undefined;
  instruments: InstrumentKey[];
  metric: RankingMetric;
  defaultInstrument?: InstrumentKey;
  totalAccountsByInstrument?: Partial<Record<InstrumentKey, number>>;
  days?: number;
  skipAnimation?: boolean;
};

export default memo(function RankHistoryChart({
  accountId,
  instruments,
  metric,
  defaultInstrument,
  totalAccountsByInstrument,
  days = 30,
  skipAnimation,
}: RankHistoryChartProps) {
  const { t } = useTranslation();
  const st = useRankHistoryChartStyles();
  const [selected, setSelected] = useState<InstrumentKey>(() => defaultInstrument ?? instruments[0] ?? 'Solo_Guitar' as InstrumentKey);

  const allHistory = useRankHistoryAll(instruments, accountId, metric, days);
  const { chartData, loading } = allHistory[selected] ?? { chartData: [], loading: true };

  const selectorItems = useMemo(
    () => instruments.map(key => ({ key })),
    [instruments],
  );

  const handleInstrumentSelect = useCallback((key: InstrumentKey) => {
    setSelected(key);
  }, []);

  const metricLabel = t(`rankings.metric.${metric}`);

  const valueTickFormatter = useCallback(
    (v: number) => formatValueTick(v, metric),
    [metric],
  );

  // Last 5 snapshots (most recent first) for the list
  const listData = useMemo(() => {
    if (chartData.length === 0) return [];
    return [...chartData].reverse().slice(0, 5);
  }, [chartData]);

  // Stable column widths derived from ALL chart history points
  const rankWidth = useMemo(() => {
    const ranks = chartData.map(p => p.rank).filter(r => r > 0);
    return computeRankWidth(ranks);
  }, [chartData]);

  const valueWidth = useMemo(() => {
    if (chartData.length === 0) return undefined;
    let maxLen = 1;
    for (const p of chartData) {
      maxLen = Math.max(maxLen, formatDetailValue(p.value, metric).length);
    }
    return Math.ceil(maxLen * Layout.rankCharWidth) + Layout.rankColumnPadding;
  }, [chartData, metric]);

  // Compute rank domain (inverted — rank 1 at top)
  const rankDomain = useMemo(() => {
    if (chartData.length === 0) return [1, 100] as [number, number];
    const ranks = chartData.map(p => p.rank).filter(r => r > 0);
    if (ranks.length === 0) return [1, 100] as [number, number];
    const minRank = Math.min(...ranks);
    const maxRank = Math.max(...ranks);
    const padded = Math.max(1, minRank - Math.ceil((maxRank - minRank) * 0.1));
    const paddedMax = maxRank + Math.ceil((maxRank - minRank) * 0.1);
    return [padded, paddedMax || 100] as [number, number]; // reversed prop on YAxis puts rank 1 at top
  }, [chartData]);

  const usePercentile = metric === 'adjusted' || metric === 'weighted';
  const totalAccounts = totalAccountsByInstrument?.[selected] ?? 0;

  const renderChart = useCallback(({ visibleData, animating, selectedPoint, setSelectedPoint }: {
    visibleData: RankHistoryChartPoint[];
    animating: boolean;
    selectedPoint: RankHistoryChartPoint | null;
    setSelectedPoint: (p: RankHistoryChartPoint | null | ((prev: RankHistoryChartPoint | null) => RankHistoryChartPoint | null)) => void;
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
          yAxisId="value"
          tick={AXIS_TICK}
          stroke={Colors.borderSubtle}
          tickFormatter={valueTickFormatter}
          label={({ viewBox }: { viewBox: { x: number; y: number; height: number } }) => {
            const cy = viewBox.y + viewBox.height / 2;
            return (
              <text x={viewBox.x - Layout.axisLabelOffset} y={cy} fill={Colors.textPrimary} fontSize={Font.md} textAnchor="middle" dominantBaseline="central" transform={`rotate(-90, ${viewBox.x - Layout.axisLabelOffset}, ${cy})`}>{metricLabel}</text>
            );
          }}
        />
        <YAxis
          yAxisId="rank"
          orientation="right"
          domain={rankDomain}
          reversed
          allowDecimals={false}
          tick={AXIS_TICK}
          stroke={Colors.borderSubtle}
          tickFormatter={(v: number) => `#${v}`}
          label={({ viewBox }: { viewBox: { x: number; y: number; width: number; height: number } }) => {
            const cy = viewBox.y + viewBox.height / 2;
            const lx = viewBox.x + viewBox.width + Layout.axisLabelOffset;
            return (
              <text x={lx} y={cy} fill={Colors.textPrimary} fontSize={Font.md} textAnchor="middle" dominantBaseline="central" transform={`rotate(90, ${lx}, ${cy})`}>{t('chart.rank')}</text>
            );
          }}
        />
        <Tooltip content={() => null} cursor={{ fill: 'transparent', stroke: 'transparent' }} trigger="click" />
        <Legend
          content={() => (
            <div style={st.legend}>
              <span style={st.legendItem}>
                <span style={{ ...st.legendSwatch, background: RANK_GRADIENT }} />
                {metricLabel}
              </span>
              <span style={st.legendItem}>
                <svg width={24} height={12} style={{ verticalAlign: 'middle' }}>
                  <line x1={0} y1={6} x2={18} y2={6} stroke={Colors.accentBlueBright} strokeWidth={2} />
                  <circle cx={18} cy={6} r={3} fill={Colors.accentBlueBright} />
                </svg>
                {t('chart.rank')}
              </span>
            </div>
          )}
        />
        {/* v8 ignore start — bar shape/click handlers */}
        {/* @ts-expect-error Recharts Bar shape/onClick types are overly strict */}
        <Bar
          yAxisId="value"
          dataKey="value"
          name={metricLabel}
          radius={Radius.barCorner}
          isAnimationActive={animating}
          animationDuration={CHART_ANIM_DURATION}
          onClick={(_data: Record<string, unknown>, index: number) => {
            const point = visibleData[index];
            if (!point) return;
            setSelectedPoint(prev => prev?.date === point.date ? null : point);
          }}
          shape={(props: Record<string, unknown>) => {
            const bar = props as { x: number; y: number; width: number; height: number; payload: RankHistoryChartPoint };
            const isSelected = selectedPoint != null && bar.payload.date === selectedPoint.date;
            const rad = Radius.barCorner[0];
            const { x, y, width: w, height: h } = bar;
            const path = `M${x + rad},${y + h} Q${x},${y + h} ${x},${y + h - rad} L${x},${y + rad} Q${x},${y} ${x + rad},${y} L${x + w - rad},${y} Q${x + w},${y} ${x + w},${y + rad} L${x + w},${y + h - rad} Q${x + w},${y + h} ${x + w - rad},${y + h} Z`;
            return (
              <path
                d={path}
                style={{ transition: transition('stroke', 150) }}
                fill={rankColor(bar.payload.rank, totalAccounts)}
                fillOpacity={0.8}
                stroke={isSelected ? Colors.accentPurple : 'transparent'}
                strokeWidth={Size.barSelectionStroke}
              />
            );
          }}
        />
        {/* v8 ignore stop */}
        <Line
          yAxisId="rank"
          type="monotone"
          dataKey="rank"
          name={t('chart.rank')}
          stroke={Colors.accentBlueBright}
          strokeWidth={2}
          dot={{ fill: Colors.accentBlueBright, r: Size.dotRadius }}
          activeDot={{ r: Size.dotRadiusActive, fill: Colors.accentBlueBright }}
          isAnimationActive={animating}
          animationDuration={CHART_ANIM_DURATION}
        />
      </ComposedChart>
    </ResponsiveContainer>
  ), [t, st, totalAccounts, metricLabel, rankDomain, valueTickFormatter]);

  const renderDetailCard = useCallback((point: RankHistoryChartPoint) => {
    const dateStr = formatSnapshotDisplayDate(point.date);
    const percentileStr = usePercentile ? formatLeaderboardPercentile(point.rank, totalAccounts) : undefined;
    const isPctMetric = metric === 'fcrate' || metric === 'maxscore';
    const pct = isPctMetric ? point.value * 100 : 0;
    return (
      <>
        <span style={{ flex: 1, color: Colors.textPrimary }}>{dateStr}</span>
        <span style={{ fontWeight: Weight.semibold, color: rankColor(point.rank, totalAccounts), width: rankWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const }}>#{point.rank.toLocaleString()}</span>
        {percentileStr
          ? <PercentilePill display={formatRatingValue(point.value)} color={rankColor(point.rank, totalAccounts)} minWidth={MetadataSize.valuePillMinWidth} />
          : isPctMetric
            ? <PercentilePill display={formatDetailValue(point.value, metric)} tier={pct >= 99 ? 'top1' : pct >= 95 ? 'top5' : 'default'} />
            : <span style={{ color: Colors.textPrimary, ...(valueWidth ? { width: valueWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const } : {}) }}>{formatDetailValue(point.value, metric)}</span>}
      </>
    );
  }, [metric, usePercentile, totalAccounts, rankWidth, valueWidth]);

  const renderListItem = useCallback((point: RankHistoryChartPoint, i: number, phase: 'idle' | 'in' | 'out') => {
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
    const dateStr = formatSnapshotDisplayDate(point.date);
    const percentileStr = usePercentile ? formatLeaderboardPercentile(point.rank, totalAccounts) : undefined;
    const isPctMetric = metric === 'fcrate' || metric === 'maxscore';
    const pct = isPctMetric ? point.value * 100 : 0;
    return (
      <div key={`${point.date}:${point.rank}:${point.value}:${i}`} style={{ ...(i === 0 ? listCardBest : listCardBase), ...animStyle }}>
        <span style={{ flex: 1, color: Colors.textPrimary, ...(i === 0 ? { fontWeight: Weight.bold } : undefined) }}>{dateStr}</span>
        <span style={{ fontWeight: i === 0 ? Weight.bold : Weight.semibold, color: rankColor(point.rank, totalAccounts), width: rankWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const }}>#{point.rank.toLocaleString()}</span>
        {percentileStr
          ? <PercentilePill display={formatRatingValue(point.value)} color={rankColor(point.rank, totalAccounts)} minWidth={MetadataSize.valuePillMinWidth} />
          : isPctMetric
            ? <PercentilePill display={formatDetailValue(point.value, metric)} tier={pct >= 99 ? 'top1' : pct >= 95 ? 'top5' : 'default'} />
            : <span style={{ color: Colors.textPrimary, ...(i === 0 ? { fontWeight: Weight.bold } : undefined), ...(valueWidth ? { width: valueWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const } : {}) }}>{formatDetailValue(point.value, metric)}</span>}
      </div>
    );
  }, [metric, usePercentile, totalAccounts, rankWidth, valueWidth]);

  if (!accountId) return null;

  return (
    <GraphCard<RankHistoryChartPoint>
      data={chartData}
      loading={loading}
      instruments={selectorItems}
      selected={selected}
      onInstrumentSelect={handleInstrumentSelect}
      title={t('chart.rankHistory')}
      subtitle={t('chart.rankHistoryHint', { days })}
      loadingMessage={t('chart.loadingRankHistory')}
      emptyMessage={t('chart.noRankHistory', { instrument: instrumentLabel(selected) })}
      identity={RANK_POINT_IDENTITY}
      renderChart={renderChart}
      renderDetailCard={renderDetailCard}
      listData={listData}
      listIdentity={RANK_POINT_IDENTITY}
      renderListItem={renderListItem}
      skipAnimation={skipAnimation}
    />
  );
});

function useRankHistoryChartStyles() {
  return useMemo(() => ({
    legend: {
      display: 'flex', justifyContent: 'center', gap: Gap.xl,
      fontSize: Font.md, color: Colors.textPrimary, paddingTop: 36,
    } as React.CSSProperties,
    legendItem: { display: 'inline-flex', alignItems: 'center', gap: Gap.sm } as React.CSSProperties,
    legendSwatch: {
      display: 'inline-block', width: Size.iconXs, height: 12, borderRadius: 2,
    } as React.CSSProperties,
  }), []);
}
