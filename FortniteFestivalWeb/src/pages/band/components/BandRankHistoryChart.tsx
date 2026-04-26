/* eslint-disable react/forbid-dom-props -- chart render props use inline styles */
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
import type { BandRankingMetric, BandType, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { formatLeaderboardPercentile, formatRatingValue, rankColor } from '@festival/core';
import {
  border,
  CHART_ANIM_DURATION,
  Colors,
  Font,
  FontVariant,
  frostedCard,
  Gap,
  Layout,
  MetadataSize,
  Radius,
  Size,
  transition,
  Weight,
} from '@festival/theme';
import GraphCard from '../../../components/common/GraphCard';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';
import { formatDetailValue, formatValueTick, type RankHistoryChartPoint } from '../../../hooks/chart/useRankHistory';
import { useBandRankHistory } from '../../../hooks/chart/useBandRankHistory';
import { parseSnapshotDate } from '../../../utils/fillRankHistoryGaps';
import { computeRankWidth } from '../../leaderboards/helpers/rankingHelpers';

const AXIS_TICK = { fill: Colors.textPrimary, fontSize: Font.md };
const X_AXIS_TICK = { ...AXIS_TICK, dy: 16 };
const X_AXIS_ANGLE = -35;
const RANK_GRADIENT = 'linear-gradient(to right, rgb(220,40,40), rgb(46,204,113))';
const GRAPH_CARD_INSTRUMENT: InstrumentKey = 'Solo_Guitar';

const RANK_POINT_IDENTITY = (a: RankHistoryChartPoint, b: RankHistoryChartPoint) =>
  a.date === b.date && a.rank === b.rank && a.value === b.value;

const formatSnapshotDisplayDate = (snapshotDate: string) =>
  parseSnapshotDate(snapshotDate).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });

const formatCountPart = (value: number | null) => value == null ? '—' : value.toLocaleString();

function getTotalSongCount(point: RankHistoryChartPoint): number | null {
  if (point.totalChartedSongs != null) return point.totalChartedSongs;
  if (point.songsPlayed == null) return null;
  if (point.coverage == null || point.coverage <= 0) return point.songsPlayed;

  const totalSongs = Math.round(point.songsPlayed / point.coverage);
  return Number.isFinite(totalSongs) && totalSongs > 0 ? totalSongs : point.songsPlayed;
}

const formatFcFraction = (point: RankHistoryChartPoint) => `${formatCountPart(point.fullComboCount)} / ${formatCountPart(getTotalSongCount(point))}`;

function renderFcFraction(point: RankHistoryChartPoint, width: number | undefined, bold = false) {
  return (
    <span style={{ color: Colors.textPrimary, ...(bold ? { fontWeight: Weight.bold } : undefined), ...(width ? { width, flexShrink: 0, fontVariantNumeric: FontVariant.tabularNums, textAlign: 'right' as const } : {}) }}>
      <span style={{ color: Colors.gold }}>{formatCountPart(point.fullComboCount)}</span>
      {` / ${formatCountPart(getTotalSongCount(point))}`}
    </span>
  );
}

const listCardBase: CSSProperties = {
  ...frostedCard,
  display: 'flex',
  alignItems: 'center',
  gap: Gap.xl,
  padding: `0 ${Gap.xl}px`,
  height: Size.iconXl,
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
  const { chartData, loading } = useBandRankHistory(bandType, teamKey, metric, days, comboId);
  const metricLabel = t(`rankings.metric.${metric}`);

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

  const listData = useMemo(() => {
    if (chartData.length === 0) return [];
    return [...chartData].reverse().slice(0, 5);
  }, [chartData]);

  const rankWidth = useMemo(() => {
    const ranks = chartData.map(p => p.rank).filter(r => r > 0);
    return computeRankWidth(ranks);
  }, [chartData]);

  const valueWidth = useMemo(() => {
    if (chartData.length === 0) return undefined;
    let maxLen = 1;
    for (const p of chartData) {
      const label = metric === 'fcrate' ? formatFcFraction(p) : formatDetailValue(p.value, metric);
      maxLen = Math.max(maxLen, label.length);
    }
    return Math.ceil(maxLen * Layout.rankCharWidth) + Layout.rankColumnPadding;
  }, [chartData, metric]);

  const rankDomain = useMemo(() => {
    if (chartData.length === 0) return [1, 100] as [number, number];
    const ranks = chartData.map(p => p.rank).filter(r => r > 0);
    if (ranks.length === 0) return [1, 100] as [number, number];
    const minRank = Math.min(...ranks);
    const maxRank = Math.max(...ranks);
    const padded = Math.max(1, minRank - Math.ceil((maxRank - minRank) * 0.1));
    const paddedMax = maxRank + Math.ceil((maxRank - minRank) * 0.1);
    return [padded, paddedMax || 100] as [number, number];
  }, [chartData]);

  const usePercentile = metric === 'adjusted' || metric === 'weighted';

  const renderChart = useCallback(({ visibleData, animating, selectedPoint, setSelectedPoint }: {
    visibleData: RankHistoryChartPoint[];
    animating: boolean;
    selectedPoint: RankHistoryChartPoint | null;
    setSelectedPoint: (p: RankHistoryChartPoint | null | ((prev: RankHistoryChartPoint | null) => RankHistoryChartPoint | null)) => void;
  }) => (
    <ResponsiveContainer width="100%" height={Size.chartHeight}>
      <ComposedChart data={visibleData} margin={Layout.chartMargin} barCategoryGap="10%">
        <CartesianGrid strokeDasharray="3 3" stroke={Colors.borderSubtle} horizontal={false} vertical={false} />
        <XAxis dataKey="dateLabel" tick={X_AXIS_TICK} stroke={Colors.borderSubtle} angle={X_AXIS_ANGLE} textAnchor="end" interval="preserveStartEnd" />
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
        <Legend content={() => (
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
        )} />
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
            return <path d={path} style={{ transition: transition('stroke', 150) }} fill={rankColor(bar.payload.rank, totalTeams)} fillOpacity={0.8} stroke={isSelected ? Colors.accentPurple : 'transparent'} strokeWidth={Size.barSelectionStroke} />;
          }}
        />
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
  ), [metricLabel, rankDomain, st, t, totalTeams, valueTickFormatter]);

  const renderDetailCard = useCallback((point: RankHistoryChartPoint) => {
    const dateStr = formatSnapshotDisplayDate(point.date);
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
    const dateStr = formatSnapshotDisplayDate(point.date);
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
      subtitle={t('band.rankHistoryHint', { days })}
      loadingMessage={t('chart.loadingRankHistory')}
      emptyMessage={t('band.noRankHistory')}
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
      width: Size.iconXs,
      height: 12,
      borderRadius: 2,
    } as CSSProperties,
  }), []);
}
