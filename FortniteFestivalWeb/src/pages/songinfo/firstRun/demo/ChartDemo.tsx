import { useRef, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { ResponsiveContainer, ComposedChart, Bar, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend } from 'recharts';
import { ACCURACY_GRADIENT, accuracyColor, Colors, Layout, MetadataSize, frostedCard, Radius, Font, Gap, Display, Align, Justify, CssValue, PointerEvents, BoxSizing, flexColumn, padding, transition, CssProp, QUICK_FADE_MS } from '@festival/theme';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useChartDimensions } from '../../../../hooks/chart/useChartDimensions';
import FadeIn from '../../../../components/page/FadeIn';
import { CHART_AXIS_TICK, CHART_X_AXIS_ANGLE, CHART_X_AXIS_TICK } from '../../../../components/common/chartVisuals';

type ChartPoint = { dateLabel: string; score: number; accuracy: number; isFullCombo: boolean };

function buildDemoData(): ChartPoint[] {
  const today = new Date();
  const fmt = (d: Date) => `${d.getMonth() + 1}/${d.getDate()}/${String(d.getFullYear()).slice(-2)}`;
  const daysAgo = (n: number) => { const d = new Date(today); d.setDate(d.getDate() - n); return d; };
  return [
    { dateLabel: fmt(daysAgo(2)), score: 218400, accuracy: 62, isFullCombo: false },
    { dateLabel: fmt(daysAgo(1)), score: 347100, accuracy: 78, isFullCombo: false },
    { dateLabel: fmt(today),      score: 486500, accuracy: 100, isFullCombo: true },
  ];
}
const DEMO_DATA: ChartPoint[] = buildDemoData();

const RAD = 4;
function CustomBar(props: { x: number; y: number; width: number; height: number; payload: ChartPoint }) {
  const { x, y, width: w, height: h, payload } = props;
  if (!h || h <= 0) return null;
  const fill = payload.accuracy >= 100 && payload.isFullCombo
    ? Colors.gold
    : accuracyColor(payload.accuracy);
  const path = `M${x + RAD},${y + h} Q${x},${y + h} ${x},${y + h - RAD} L${x},${y + RAD} Q${x},${y} ${x + RAD},${y} L${x + w - RAD},${y} Q${x + w},${y} ${x + w},${y + RAD} L${x + w},${y + h - RAD} Q${x + w},${y + h} ${x + w - RAD},${y + h} Z`;
  return <path d={path} fill={fill} fillOpacity={1} stroke="transparent" strokeWidth={0} />;
}

export default function ChartDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();
  const chartContainerRef = useRef<HTMLDivElement>(null);
  const { maxBars } = useChartDimensions(chartContainerRef);

  const visibleData = useMemo(
    () => DEMO_DATA.slice(Math.max(0, DEMO_DATA.length - maxBars)),
    [maxBars],
  );

  const hasFc = visibleData.some(p => p.accuracy >= 100 && p.isFullCombo);
  const hasNonFc = visibleData.some(p => !(p.accuracy >= 100 && p.isFullCombo));

  const s = useChartDemoStyles();

  return (
    <div style={h ? { ...s.wrapper, height: h } : s.wrapper}>
      <FadeIn delay={0} style={{ flex: 1, minHeight: 0 }}>
        <div style={s.chartCardNoEvents} ref={chartContainerRef}>
          <ResponsiveContainer width="100%" height="100%">
            <ComposedChart data={visibleData} margin={Layout.chartMargin} barCategoryGap="10%">
              <CartesianGrid strokeDasharray="3 3" stroke={Colors.borderSubtle} horizontal={false} vertical={false} />
              <XAxis
                dataKey="dateLabel"
                tick={CHART_X_AXIS_TICK}
                stroke={Colors.borderSubtle}
                angle={CHART_X_AXIS_ANGLE}
                textAnchor="end"
                interval="preserveStartEnd"
                height={Layout.chartXAxisHeight}
              />
              <YAxis
                yAxisId="score"
                tick={CHART_AXIS_TICK}
                stroke={Colors.borderSubtle}
                tickFormatter={(v: number) => v >= 1000 ? `${(v / 1000).toFixed(0)}k` : String(v)}
                label={({ viewBox }: { viewBox: { x: number; y: number; height: number } }) => {
                  const cy = viewBox.y + viewBox.height / 2;
                  return (
                    <text x={viewBox.x - Layout.axisLabelOffset} y={cy} fill={Colors.textPrimary} fontSize={Layout.chartTickFontSize} textAnchor="middle" dominantBaseline="central" transform={`rotate(-90, ${viewBox.x - Layout.axisLabelOffset}, ${cy})`}>
                      {t('chart.score')}
                    </text>
                  );
                }}
              />
              <YAxis
                yAxisId="accuracy"
                orientation="right"
                domain={[0, 100]}
                padding={{ top: 4 }}
                tick={CHART_AXIS_TICK}
                stroke={Colors.borderSubtle}
                tickFormatter={(v: number) => `${v}%`}
                label={({ viewBox }: { viewBox: { x: number; y: number; width: number; height: number } }) => {
                  const cy = viewBox.y + viewBox.height / 2;
                  const lx = viewBox.x + viewBox.width + Layout.axisLabelOffset;
                  return (
                    <text x={lx} y={cy} fill={Colors.textPrimary} fontSize={Layout.chartTickFontSize} textAnchor="middle" dominantBaseline="central" transform={`rotate(90, ${lx}, ${cy})`}>
                      {t('chart.accuracy')}
                    </text>
                  );
                }}
              />
              <Tooltip content={() => null} cursor={{ fill: 'transparent', stroke: 'transparent' }} />
              <Legend content={() => (
                <div style={s.legend}>
                  {hasNonFc && (
                    <span style={s.legendItem}>
                      <span style={s.legendSwatchGradient} />
                      {t('chart.accuracy')}
                    </span>
                  )}
                  {hasFc && (
                    <span style={s.legendItem}>
                      <span style={s.legendSwatchGold} />
                      {t('chart.accuracyFC')}
                    </span>
                  )}
                  <span style={s.legendItem}>
                    <svg width={24} height={12} style={{ verticalAlign: 'middle' }}>
                      <line x1={0} y1={6} x2={18} y2={6} stroke={Colors.accentBlueBright} strokeWidth={2} />
                      <circle cx={18} cy={6} r={3} fill={Colors.accentBlueBright} />
                    </svg>
                    {t('chart.score')}
                  </span>
                </div>
              )} />
              <Bar yAxisId="accuracy" dataKey="accuracy" radius={[RAD, RAD, 0, 0]} isAnimationActive={false} shape={CustomBar as any} />
              <Line yAxisId="score" type="monotone" dataKey="score" stroke={Colors.accentBlueBright} strokeWidth={2} dot={{ fill: Colors.accentBlueBright, r: MetadataSize.dotRadius }} isAnimationActive={false} />
            </ComposedChart>
          </ResponsiveContainer>
        </div>
      </FadeIn>
    </div>
  );
}

/** Shared styles exported for BarSelectDemo cross-consumer usage. */
export function useChartDemoStyles() {
  return useMemo(() => {
    const chartCard: CSSProperties = {
      ...frostedCard,
      borderRadius: Radius.lg,
      padding: padding(Gap.sm, Gap.md, Gap.md),
      ...flexColumn,
    };
    return {
      wrapper: { width: CssValue.full, ...flexColumn, pointerEvents: PointerEvents.none } as CSSProperties,
      chartCard,
      chartCardNoEvents: { ...chartCard, pointerEvents: PointerEvents.none, height: CssValue.full } as CSSProperties,
      legend: { display: Display.flex, justifyContent: Justify.center, gap: Gap.xl, paddingTop: Gap.xs } as CSSProperties,
      legendItem: { display: Display.inlineFlex, alignItems: Align.center, gap: Gap.xs, fontSize: Font.xs, color: Colors.textMuted } as CSSProperties,
      detailCardBorderless: {
        display: Display.flex,
        alignItems: Align.center,
        gap: Gap.xl,
        padding: padding(0, Gap.md),
        height: Layout.entryRowHeight,
        fontSize: Font.md,
        color: CssValue.inherit,
        width: CssValue.full,
        boxSizing: BoxSizing.borderBox,
        transition: transition(CssProp.opacity, QUICK_FADE_MS),
      } as CSSProperties,
      legendSwatchBase: {
        display: Display.inlineBlock,
        width: Layout.legendSwatchSize,
        height: Layout.legendSwatchSize,
        borderRadius: Gap.xs,
      } as CSSProperties,
      legendSwatchGradient: {
        display: Display.inlineBlock,
        width: Layout.legendSwatchSize,
        height: Layout.legendSwatchSize,
        borderRadius: Gap.xs,
        background: ACCURACY_GRADIENT,
      } as CSSProperties,
      legendSwatchGold: {
        display: Display.inlineBlock,
        width: Layout.legendSwatchSize,
        height: Layout.legendSwatchSize,
        borderRadius: Gap.xs,
        backgroundColor: Colors.gold,
      } as CSSProperties,
    };
  }, []);
}
