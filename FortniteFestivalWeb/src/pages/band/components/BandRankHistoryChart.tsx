import { memo, useCallback, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Bar,
  CartesianGrid,
  ComposedChart,
  Legend,
  Line,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import type { BandRankingMetric, BandType, ServerInstrumentKey as InstrumentKey } from '@festival/core/api';
import { formatLeaderboardPercentile, formatRatingValue, rankColor } from '@festival/core/app/formatters';
import {
  border,
  CHART_ANIM_DURATION,
  Colors,
  Font,
  FontVariant,
  frostedCard,
  Gap,
  IconSize,
  Layout,
  MetadataSize,
  Radius,
  ChartSize,
  transition,
  Weight,
  ACCURACY_GRADIENT,
} from '@festival/theme';
import GraphCard from '../../../components/common/GraphCard';
import { PressableChartPath } from '../../../components/common/PressableChartPath';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';
import { useBandRankHistory } from '../../../hooks/chart/useBandRankHistory';
import { computeRankAxisWidth, computeRankWidth, formatRankLabel } from '../../leaderboards/helpers/rankingHelpers';
import { CHART_AXIS_TICK, CHART_X_AXIS_ANGLE, CHART_X_AXIS_TICK } from '../../../components/common/chartVisuals';
import {
  formatDetailValue,
  formatRankHistoryCount,
  formatRankHistoryDisplayDate,
  formatRankHistoryFcFraction,
  formatValueTick,
  getRankHistoryDomain,
  getRankHistoryTotalSongCount,
  getRecentRankHistoryPoints,
  isSameRankHistoryPoint,
  type RankHistoryChartPoint,
} from '../../../utils/rankHistoryChartModel';
const GRAPH_CARD_INSTRUMENT: InstrumentKey = 'Solo_Guitar';

function renderFcFraction(point: RankHistoryChartPoint, width: number | undefined, bold = false) {
  return (
    <span style={{ color: Colors.textPrimary, ...(bold ? { fontWeight: Weight.bold } : undefined), ...(width ? { width, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const } : {}) }}>
      <span style={{ color: Colors.gold }}>{formatRankHistoryCount(point.fullComboCount)}</span>
      {` / ${formatRankHistoryCount(getRankHistoryTotalSongCount(point))}`}
    </span>
  );
}

const listCardBase: CSSProperties = {
  ...frostedCard,
  display: 'flex',
  alignItems: 'center',
  gap: Gap.xl,
  padding: `0 ${Gap.xl}px`,
  height: IconSize.xl,
  borderRadius: Radius.md,
  fontSize: Font.md,
  color: 'inherit',
  transition: transition('border-color', 150),
};
const listCardBest: CSSProperties = {
  ...listCardBase,
  backgroundColor: Colors.purpleHighlight,
  border: border(1, Colors.purpleHighlightBorder),
};

export type BandRankHistoryChartProps = {
  bandType: BandType | undefined;
  teamKey: string | undefined;
  totalRankedTeams?: number | null;
  metric?: BandRankingMetric;
  days?: number;
  comboId?: string;
  skipAnimation?: boolean;
};

export default memo(function BandRankHistoryChart({
  bandType,
  teamKey,
  totalRankedTeams,
  metric = 'adjusted',
  days = 30,
  comboId,
  skipAnimation,
}: BandRankHistoryChartProps) {
  const { t } = useTranslation();
  const st = useBandRankHistoryChartStyles();
  const { chartData, loading, historyStatus, historyMessage } = useBandRankHistory(bandType, teamKey, metric, days, comboId);
  const metricLabel = t(`rankings.metric.${metric}`);

  const statusMessage = useMemo(() => {
    if (historyStatus === 'failed') return null;
    if (historyMessage) return historyMessage;
    switch (historyStatus) {
      case 'catching_up': return t('band.rankHistoryCatchingUp');
      case 'stale': return t('band.rankHistoryStale');
      case 'disabled': return t('band.rankHistoryDisabled');
      default: return null;
    }
  }, [historyMessage, historyStatus, t]);

  const subtitle = useMemo(() => {
    const base = t('band.rankHistoryHint', { days });
    return statusMessage ? `${base} ${statusMessage}` : base;
  }, [days, statusMessage, t]);

  const totalTeams = useMemo(() => {
    if (totalRankedTeams != null && totalRankedTeams > 0) return totalRankedTeams;
    for (let i = chartData.length - 1; i >= 0; i--) {
      const count = chartData[i]?.rankedAccountCount;
      if (count != null && count > 0) return count;
    }
    return 0;
  }, [chartData, totalRankedTeams]);

  const valueTickFormatter = useCallback(
    (v: number) => formatValueTick(v, metric),
    [metric],
  );

  const listData = useMemo(
    () => getRecentRankHistoryPoints(chartData),
    [chartData],
  );

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

  const rankDomain = useMemo(
    () => getRankHistoryDomain(chartData),
    [chartData],
  );

  const rankAxisWidth = useMemo(() => {
    const ranks = chartData.map(p => p.rank).filter(r => r > 0);
    return computeRankAxisWidth(ranks, rankDomain);
  }, [chartData, rankDomain]);

  const usePercentile = metric === 'adjusted' || metric === 'weighted';

  const renderChart = useCallback(({ visibleData, animating, selectedPoint, setSelectedPoint }: {
    visibleData: RankHistoryChartPoint[];
    animating: boolean;
    selectedPoint: RankHistoryChartPoint | null;
    setSelectedPoint: (p: RankHistoryChartPoint | null | ((prev: RankHistoryChartPoint | null) => RankHistoryChartPoint | null)) => void;
  }) => (
    <ResponsiveContainer width="100%" height={ChartSize.height}>
      <ComposedChart data={visibleData} margin={Layout.chartMargin} barCategoryGap="10%">
        <CartesianGrid strokeDasharray="3 3" stroke={Colors.borderSubtle} horizontal={false} vertical={false} />
        <XAxis dataKey="dateLabel" tick={CHART_X_AXIS_TICK} stroke={Colors.borderSubtle} angle={CHART_X_AXIS_ANGLE} textAnchor="end" interval="preserveStartEnd" />
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
        <Legend content={() => (
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
        )} />
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
                fill={rankColor(bar.payload.rank, totalTeams)}
                fillOpacity={0.8}
                stroke={isSelected ? Colors.accentPurple : 'transparent'}
                strokeWidth={ChartSize.barSelectionStroke}
                onPress={() => setSelectedPoint(prev => prev?.date === bar.payload.date ? null : bar.payload)}
              />
            );
          }}
        />
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
  ), [metricLabel, rankAxisWidth, rankDomain, st, t, totalTeams, valueTickFormatter]);

  const renderDetailCard = useCallback((point: RankHistoryChartPoint) => {
    const dateStr = formatRankHistoryDisplayDate(point.date);
    const percentileStr = usePercentile ? formatLeaderboardPercentile(point.rank, totalTeams) : undefined;
    const isPctMetric = metric === 'fcrate';
    const pct = isPctMetric ? point.value * 100 : 0;
    return (
      <>
        <span style={{ flex: 1, color: Colors.textPrimary }}>{dateStr}</span>
        <span style={{ fontWeight: Weight.semibold, color: rankColor(point.rank, totalTeams), width: rankWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const }}>#{point.rank.toLocaleString()}</span>
        {percentileStr
          ? <PercentilePill display={formatRatingValue(point.value)} color={rankColor(point.rank, totalTeams)} minWidth={MetadataSize.valuePillMinWidth} />
          : metric === 'fcrate'
            ? renderFcFraction(point, valueWidth)
            : isPctMetric
              ? <PercentilePill display={formatDetailValue(point.value, metric)} tier={pct >= 99 ? 'top1' : pct >= 95 ? 'top5' : 'default'} />
              : <span style={{ color: Colors.textPrimary, ...(valueWidth ? { width: valueWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const } : {}) }}>{formatDetailValue(point.value, metric)}</span>}
      </>
    );
  }, [metric, rankWidth, totalTeams, usePercentile, valueWidth]);

  const renderListItem = useCallback((point: RankHistoryChartPoint, i: number, phase: 'idle' | 'in' | 'out') => {
    let animStyle: CSSProperties = {};
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
    const dateStr = formatRankHistoryDisplayDate(point.date);
    const percentileStr = usePercentile ? formatLeaderboardPercentile(point.rank, totalTeams) : undefined;
    const isPctMetric = metric === 'fcrate';
    const pct = isPctMetric ? point.value * 100 : 0;
    return (
      <div key={`${point.date}:${point.rank}:${point.value}:${i}`} style={{ ...(i === 0 ? listCardBest : listCardBase), ...animStyle }}>
        <span style={{ flex: 1, color: Colors.textPrimary, ...(i === 0 ? { fontWeight: Weight.bold } : undefined) }}>{dateStr}</span>
        <span style={{ fontWeight: i === 0 ? Weight.bold : Weight.semibold, color: rankColor(point.rank, totalTeams), width: rankWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const }}>#{point.rank.toLocaleString()}</span>
        {percentileStr
          ? <PercentilePill display={formatRatingValue(point.value)} color={rankColor(point.rank, totalTeams)} minWidth={MetadataSize.valuePillMinWidth} />
          : metric === 'fcrate'
            ? renderFcFraction(point, valueWidth, i === 0)
            : isPctMetric
              ? <PercentilePill display={formatDetailValue(point.value, metric)} tier={pct >= 99 ? 'top1' : pct >= 95 ? 'top5' : 'default'} />
              : <span style={{ color: Colors.textPrimary, ...(i === 0 ? { fontWeight: Weight.bold } : undefined), ...(valueWidth ? { width: valueWidth, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const } : {}) }}>{formatDetailValue(point.value, metric)}</span>}
      </div>
    );
  }, [metric, rankWidth, totalTeams, usePercentile, valueWidth]);

  if (!bandType || !teamKey) return null;

  return (
    <GraphCard<RankHistoryChartPoint>
      data={chartData}
      loading={loading}
      instruments={[]}
      selected={GRAPH_CARD_INSTRUMENT}
      onInstrumentSelect={() => {}}
      title={t('band.rankHistory')}
      subtitle={subtitle}
      loadingMessage={t('chart.loadingRankHistory')}
      emptyMessage={t('band.noRankHistory')}
      identity={isSameRankHistoryPoint}
      renderChart={renderChart}
      renderDetailCard={renderDetailCard}
      listData={listData}
      listIdentity={isSameRankHistoryPoint}
      renderListItem={renderListItem}
      skipAnimation={skipAnimation}
    />
  );
});

function useBandRankHistoryChartStyles() {
  return useMemo(() => ({
    legend: {
      display: 'flex',
      justifyContent: 'center',
      gap: Gap.xl,
      fontSize: Font.md,
      color: Colors.textPrimary,
      paddingTop: 36,
    } as CSSProperties,
    legendItem: { display: 'inline-flex', alignItems: 'center', gap: Gap.sm } as CSSProperties,
    legendSwatch: {
      display: 'inline-block',
      width: IconSize.xs,
      height: 12,
      borderRadius: 2,
    } as CSSProperties,
  }), []);
}
