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
import type { ServerInstrumentKey as InstrumentKey, RankingMetric } from '@festival/core/api';
import { serverInstrumentLabel as instrumentLabel } from '@festival/core/api';
import { formatLeaderboardPercentile, formatRatingValue, rankColor } from '@festival/core/app/formatters';
import GraphCard from '../../../components/common/GraphCard';
import { PressableChartPath } from '../../../components/common/PressableChartPath';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';
import { useRankHistoryAll } from '../../../hooks/chart/useRankHistory';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { COMPACT_PERCENTILE_ROW_HEIGHT } from './RankingEntry';
import { computePillMinWidth, computeRankAxisWidth, computeRankWidth, formatBayesianRatingDisplay, formatRankLabel, formatRankingValueDisplay } from '../helpers/rankingHelpers';
import {
  ACCURACY_GRADIENT, ChartSize, Colors, Font, FontVariant, Gap, IconSize, Layout, MetadataSize, Radius, Weight,
  frostedCard, padding, border, transition, truncate,
  CHART_ANIM_DURATION,
} from '@festival/theme';
import { CHART_AXIS_TICK, CHART_X_AXIS_ANGLE, CHART_X_AXIS_TICK } from '../../../components/common/chartVisuals';
import {
  formatDetailValue,
  formatRankHistoryCount,
  formatRankHistoryDisplayDate,
  formatRankHistoryFcFraction,
  formatRankHistorySongsFraction,
  formatValueTick,
  getRankHistoryDomain,
  getRankHistoryTotalSongCount,
  getRecentRankHistoryPoints,
  isSameRankHistoryPoint,
  type RankHistoryChartPoint,
} from '../../../utils/rankHistoryChartModel';

function renderFcFraction(point: RankHistoryChartPoint, width: number | undefined, bold = false) {
  return (
    <span style={{ color: Colors.textPrimary, ...(bold ? { fontWeight: Weight.bold } : undefined), ...(width ? { width, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const } : {}) }}>
      <span style={{ color: Colors.gold }}>{formatRankHistoryCount(point.fullComboCount)}</span>
      {` / ${formatRankHistoryCount(getRankHistoryTotalSongCount(point))}`}
    </span>
  );
}

/* ── List card styles ── */
const listCardBase: React.CSSProperties = {
  ...frostedCard, display: 'flex', alignItems: 'center', gap: Gap.xl,
  padding: padding(0, Gap.xl), height: IconSize.xl, borderRadius: Radius.md,
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
  const isMobile = useIsMobile();
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
  const listData = useMemo(
    () => getRecentRankHistoryPoints(chartData),
    [chartData],
  );

  // Stable column widths derived from ALL chart history points
  const rankWidth = useMemo(() => {
    const ranks = chartData.map(p => p.rank).filter(r => r > 0);
    return computeRankWidth(ranks);
  }, [chartData]);

  const valueWidth = useMemo(() => {
    if (chartData.length === 0) return undefined;
    let maxLen = 1;
    for (const p of chartData) {
      const label = metric === 'fcrate' ? formatRankHistoryFcFraction(p) : formatDetailValue(p.value, metric);
      maxLen = Math.max(maxLen, label.length);
    }
    return Math.ceil(maxLen * Layout.rankCharWidth) + Layout.rankColumnPadding;
  }, [chartData, metric]);

  // Compute rank domain (inverted — rank 1 at top)
  const rankDomain = useMemo(
    () => getRankHistoryDomain(chartData),
    [chartData],
  );

  const rankAxisWidth = useMemo(() => {
    const ranks = chartData.map(p => p.rank).filter(r => r > 0);
    return computeRankAxisWidth(ranks, rankDomain);
  }, [chartData, rankDomain]);

  const usePercentile = metric === 'adjusted' || metric === 'weighted';
  const totalAccounts = totalAccountsByInstrument?.[selected] ?? 0;
  const showMobilePercentileDetails = isMobile && usePercentile;
  const historyTwoRowHeight = COMPACT_PERCENTILE_ROW_HEIGHT;

  const percentileValueWidth = useMemo(() => {
    if (!usePercentile) return undefined;
    return computePillMinWidth(chartData.map(point => formatRankingValueDisplay(point.value, metric)));
  }, [chartData, metric, usePercentile]);

  const bayesianValueWidth = useMemo(() => {
    if (!usePercentile) return undefined;
    return computePillMinWidth(chartData.map(point => formatBayesianRatingDisplay(point.bayesianValue ?? point.value, metric)));
  }, [chartData, metric, usePercentile]);

  const renderChart = useCallback(({ visibleData, animating, selectedPoint, setSelectedPoint }: {
    visibleData: RankHistoryChartPoint[];
    animating: boolean;
    selectedPoint: RankHistoryChartPoint | null;
    setSelectedPoint: (p: RankHistoryChartPoint | null | ((prev: RankHistoryChartPoint | null) => RankHistoryChartPoint | null)) => void;
  }) => (
    <ResponsiveContainer width="100%" height={ChartSize.height}>
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
          tick={CHART_X_AXIS_TICK}
          stroke={Colors.borderSubtle}
          angle={CHART_X_AXIS_ANGLE}
          textAnchor="end"
          interval="preserveStartEnd"
        />
        <YAxis
          yAxisId="value"
          tick={CHART_AXIS_TICK}
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
          width={rankAxisWidth}
          tick={CHART_AXIS_TICK}
          stroke={Colors.borderSubtle}
          tickFormatter={(v: number) => formatRankLabel(v)}
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
                <span style={{ ...st.legendSwatch, background: ACCURACY_GRADIENT }} />
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
          shape={(props: Record<string, unknown>) => {
            const bar = props as { x: number; y: number; width: number; height: number; payload: RankHistoryChartPoint };
            const isSelected = selectedPoint != null && bar.payload.date === selectedPoint.date;
            const rad = Radius.barCorner[0];
            const { x, y, width: w, height: h } = bar;
            const path = `M${x + rad},${y + h} Q${x},${y + h} ${x},${y + h - rad} L${x},${y + rad} Q${x},${y} ${x + rad},${y} L${x + w - rad},${y} Q${x + w},${y} ${x + w},${y + rad} L${x + w},${y + h - rad} Q${x + w},${y + h} ${x + w - rad},${y + h} Z`;
            return (
              <PressableChartPath
                ariaLabel={`${metricLabel}: ${bar.payload.dateLabel}`}
                d={path}
                style={{ transition: transition('stroke', 150) }}
                fill={rankColor(bar.payload.rank, totalAccounts)}
                fillOpacity={0.8}
                stroke={isSelected ? Colors.accentPurple : 'transparent'}
                strokeWidth={ChartSize.barSelectionStroke}
                onPress={() => setSelectedPoint(prev => prev?.date === bar.payload.date ? null : bar.payload)}
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
          dot={{ fill: Colors.accentBlueBright, r: MetadataSize.dotRadius }}
          activeDot={{ r: MetadataSize.dotRadiusActive, fill: Colors.accentBlueBright }}
          isAnimationActive={animating}
          animationDuration={CHART_ANIM_DURATION}
        />
      </ComposedChart>
    </ResponsiveContainer>
  ), [t, st, totalAccounts, metricLabel, rankAxisWidth, rankDomain, valueTickFormatter]);

  const renderDetailCard = useCallback((point: RankHistoryChartPoint) => {
    const dateStr = formatRankHistoryDisplayDate(point.date);
    const percentileStr = usePercentile ? formatLeaderboardPercentile(point.rank, totalAccounts) : undefined;
    const isPctMetric = metric === 'fcrate' || metric === 'maxscore';
    const pct = isPctMetric ? point.value * 100 : 0;
    if (showMobilePercentileDetails) {
      return (
        <div style={st.mobileHistoryLayout}>
          <div data-testid="rank-history-compact-primary-row" style={st.mobileHistoryPrimary}>
            <div style={st.mobileHistoryIdentity}>
              <span style={st.mobileHistoryLabel}>{dateStr}</span>
              <span style={{ fontWeight: Weight.semibold, color: rankColor(point.rank, totalAccounts), width: rankWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const }}>#{point.rank.toLocaleString()}</span>
            </div>
            <div data-testid="rank-history-compact-primary-metadata" style={st.mobileHistoryPrimaryMetadata}>
              {renderPercentileHistoryPrimaryMetadata(point, metric, percentileValueWidth)}
            </div>
          </div>
          <div data-testid="rank-history-compact-bayesian-row" style={st.mobileHistoryBayesianMetadata}>
            {renderBayesianHistoryMetadata(point, metric, totalAccounts, bayesianValueWidth)}
          </div>
        </div>
      );
    }
    return (
      <>
        <span style={{ flex: 1, color: Colors.textPrimary }}>{dateStr}</span>
        <span style={{ fontWeight: Weight.semibold, color: rankColor(point.rank, totalAccounts), width: rankWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const }}>#{point.rank.toLocaleString()}</span>
        {percentileStr
          ? <PercentilePill display={formatRatingValue(point.value)} color={rankColor(point.rank, totalAccounts)} minWidth={MetadataSize.valuePillMinWidth} />
          : metric === 'fcrate'
            ? renderFcFraction(point, valueWidth)
            : isPctMetric
            ? <PercentilePill display={formatDetailValue(point.value, metric)} tier={pct >= 99 ? 'top1' : pct >= 95 ? 'top5' : 'default'} />
            : <span style={{ color: Colors.textPrimary, ...(valueWidth ? { width: valueWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const } : {}) }}>{formatDetailValue(point.value, metric)}</span>}
      </>
    );
  }, [bayesianValueWidth, metric, percentileValueWidth, rankWidth, showMobilePercentileDetails, st.mobileHistoryLayout, st.mobileHistoryPrimary, totalAccounts, usePercentile, valueWidth]);

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
    const dateStr = formatRankHistoryDisplayDate(point.date);
    const percentileStr = usePercentile ? formatLeaderboardPercentile(point.rank, totalAccounts) : undefined;
    const isPctMetric = metric === 'fcrate' || metric === 'maxscore';
    const pct = isPctMetric ? point.value * 100 : 0;
    if (showMobilePercentileDetails) {
      return (
        <div key={`${point.date}:${point.rank}:${point.value}:${i}`} style={{ ...(i === 0 ? listCardBest : listCardBase), ...st.mobileHistoryListCard, ...animStyle }}>
          <div data-testid="rank-history-compact-primary-row" style={st.mobileHistoryPrimary}>
            <div style={st.mobileHistoryIdentity}>
              <span style={{ ...st.mobileHistoryLabel, ...(i === 0 ? { fontWeight: Weight.bold } : undefined) }}>{dateStr}</span>
              <span style={{ fontWeight: i === 0 ? Weight.bold : Weight.semibold, color: rankColor(point.rank, totalAccounts), width: rankWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const }}>#{point.rank.toLocaleString()}</span>
            </div>
            <div data-testid="rank-history-compact-primary-metadata" style={st.mobileHistoryPrimaryMetadata}>
              {renderPercentileHistoryPrimaryMetadata(point, metric, percentileValueWidth, i === 0)}
            </div>
          </div>
          <div data-testid="rank-history-compact-bayesian-row" style={st.mobileHistoryBayesianMetadata}>
            {renderBayesianHistoryMetadata(point, metric, totalAccounts, bayesianValueWidth, i === 0)}
          </div>
        </div>
      );
    }
    return (
      <div key={`${point.date}:${point.rank}:${point.value}:${i}`} style={{ ...(i === 0 ? listCardBest : listCardBase), ...animStyle }}>
        <span style={{ flex: 1, color: Colors.textPrimary, ...(i === 0 ? { fontWeight: Weight.bold } : undefined) }}>{dateStr}</span>
        <span style={{ fontWeight: i === 0 ? Weight.bold : Weight.semibold, color: rankColor(point.rank, totalAccounts), width: rankWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const }}>#{point.rank.toLocaleString()}</span>
        {percentileStr
          ? <PercentilePill display={formatRatingValue(point.value)} color={rankColor(point.rank, totalAccounts)} minWidth={MetadataSize.valuePillMinWidth} />
          : metric === 'fcrate'
            ? renderFcFraction(point, valueWidth, i === 0)
            : isPctMetric
            ? <PercentilePill display={formatDetailValue(point.value, metric)} tier={pct >= 99 ? 'top1' : pct >= 95 ? 'top5' : 'default'} />
            : <span style={{ color: Colors.textPrimary, ...(i === 0 ? { fontWeight: Weight.bold } : undefined), ...(valueWidth ? { width: valueWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const } : {}) }}>{formatDetailValue(point.value, metric)}</span>}
      </div>
    );
  }, [bayesianValueWidth, metric, percentileValueWidth, rankWidth, showMobilePercentileDetails, st.mobileHistoryListCard, st.mobileHistoryPrimary, totalAccounts, usePercentile, valueWidth]);

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
      identity={isSameRankHistoryPoint}
      renderChart={renderChart}
      renderDetailCard={renderDetailCard}
      listData={listData}
      listIdentity={isSameRankHistoryPoint}
      renderListItem={renderListItem}
      listCardHeight={showMobilePercentileDetails ? historyTwoRowHeight : undefined}
      skipAnimation={skipAnimation}
    />
  );
});

function renderPercentileHistoryPrimaryMetadata(
  point: RankHistoryChartPoint,
  metric: RankingMetric,
  percentileValueWidth: number | undefined,
  bold = false,
) {
  return (
    <>
      <span style={{ flexShrink: 0, fontSize: Font.sm, color: Colors.textSecondary, fontVariantNumeric: FontVariant.tabularNums, ...(bold ? { fontWeight: Weight.bold } : undefined) }}>{formatRankHistorySongsFraction(point)}</span>
      <PercentilePill display={formatRankingValueDisplay(point.value, metric)} minWidth={percentileValueWidth} bold={bold} />
    </>
  );
}

function renderBayesianHistoryMetadata(
  point: RankHistoryChartPoint,
  metric: RankingMetric,
  totalAccounts: number,
  bayesianValueWidth: number | undefined,
  bold = false,
) {
  return (
    <>
      <span style={{ flexShrink: 0, fontSize: Font.sm, color: Colors.textSecondary, fontWeight: bold ? Weight.bold : Weight.semibold }}>Bayesian-Calculated Rank:</span>
      <PercentilePill display={formatBayesianRatingDisplay(point.bayesianValue ?? point.value, metric)} color={rankColor(point.rank, totalAccounts)} minWidth={bayesianValueWidth ?? MetadataSize.valuePillMinWidth} bold={bold} />
    </>
  );
}

function useRankHistoryChartStyles() {
  return useMemo(() => ({
    legend: {
      display: 'flex', justifyContent: 'center', gap: Gap.xl,
      fontSize: Font.md, color: Colors.textPrimary, paddingTop: 36,
    } as React.CSSProperties,
    legendItem: { display: 'inline-flex', alignItems: 'center', gap: Gap.sm } as React.CSSProperties,
    legendSwatch: {
      display: 'inline-block', width: IconSize.xs, height: 12, borderRadius: 2,
    } as React.CSSProperties,
    mobileHistoryLayout: {
      display: 'flex',
      flexDirection: 'column',
      justifyContent: 'center',
      gap: Gap.xl,
      width: '100%',
      minWidth: 0,
    } as React.CSSProperties,
    mobileHistoryPrimary: {
      display: 'flex',
      alignItems: 'center',
      gap: Gap.md,
      width: '100%',
      minWidth: 0,
    } as React.CSSProperties,
    mobileHistoryIdentity: {
      display: 'flex',
      alignItems: 'center',
      gap: Gap.xl,
      flex: 1,
      minWidth: 0,
    } as React.CSSProperties,
    mobileHistoryLabel: {
      ...truncate,
      flex: 1,
      minWidth: 0,
      color: Colors.textPrimary,
    } as React.CSSProperties,
    mobileHistoryPrimaryMetadata: {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'flex-end',
      gap: Gap.md,
      minWidth: 0,
      flexShrink: 0,
    } as React.CSSProperties,
    mobileHistoryBayesianMetadata: {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'flex-end',
      gap: Gap.md,
      width: '100%',
      minWidth: 0,
    } as React.CSSProperties,
    mobileHistoryListCard: {
      height: 'auto',
      minHeight: Layout.entryRowHeight + 28,
      flexDirection: 'column',
      alignItems: 'stretch',
      justifyContent: 'center',
      gap: Gap.xl,
      padding: padding(Gap.sm, Gap.xl),
      boxSizing: 'border-box',
    } as React.CSSProperties,
  }), []);
}
