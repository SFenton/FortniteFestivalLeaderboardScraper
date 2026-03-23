/* eslint-disable react/forbid-dom-props -- dynamic styles required for Recharts custom elements */
import { useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { ResponsiveContainer, ComposedChart, Bar, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend } from 'recharts';
import { Colors, Layout, Size } from '@festival/theme';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useChartDimensions } from '../../../../hooks/chart/useChartDimensions';
import FadeIn from '../../../../components/page/FadeIn';
import css from './ChartDemo.module.css';

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

function barFill(accuracy: number, isFullCombo: boolean): string {
  if (accuracy >= 100 && isFullCombo) return Colors.gold;
  const t = Math.min(Math.max(accuracy / 100, 0), 1);
  const r = Math.round(220 * (1 - t) + 46 * t);
  const g = Math.round(40 * (1 - t) + 204 * t);
  const b = Math.round(40 * (1 - t) + 113 * t);
  return `rgb(${r},${g},${b})`;
}

const RAD = 4;
function CustomBar(props: { x: number; y: number; width: number; height: number; payload: ChartPoint }) {
  const { x, y, width: w, height: h, payload } = props;
  /* v8 ignore next -- Recharts shape guard; bars always have positive height when data is valid */
  if (!h || h <= 0) return null;
  const fill = barFill(payload.accuracy, payload.isFullCombo);
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

  return (
    <div className={css.wrapper} style={h ? { height: h } : undefined}>
      <FadeIn delay={0} style={{ flex: 1, minHeight: 0 }}>
        <div className={css.chartCard} ref={chartContainerRef} style={{ pointerEvents: 'none', height: '100%' }}>
          <ResponsiveContainer width="100%" height="100%">
            <ComposedChart data={visibleData} margin={Layout.chartMargin} barCategoryGap="10%">
              <CartesianGrid strokeDasharray="3 3" stroke={Colors.borderSubtle} horizontal={false} vertical={false} />
              <XAxis
                dataKey="dateLabel"
                tick={{ fill: Colors.textPrimary, fontSize: 14, dy: 16 }}
                stroke={Colors.borderSubtle}
                angle={-35}
                textAnchor="end"
                interval="preserveStartEnd"
                height={60}
              />
              <YAxis
                yAxisId="score"
                tick={{ fill: Colors.textPrimary, fontSize: 14 }}
                stroke={Colors.borderSubtle}
                tickFormatter={(v: number) => v >= 1000 ? `${(v / 1000).toFixed(0)}k` : String(v)}
                label={({ viewBox }: { viewBox: { x: number; y: number; height: number } }) => {
                  const cy = viewBox.y + viewBox.height / 2;
                  return (
                    <text x={viewBox.x - Layout.axisLabelOffset} y={cy} fill={Colors.textPrimary} fontSize={14} textAnchor="middle" dominantBaseline="central" transform={`rotate(-90, ${viewBox.x - Layout.axisLabelOffset}, ${cy})`}>
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
                tick={{ fill: Colors.textPrimary, fontSize: 14 }}
                stroke={Colors.borderSubtle}
                tickFormatter={(v: number) => `${v}%`}
                label={({ viewBox }: { viewBox: { x: number; y: number; width: number; height: number } }) => {
                  const cy = viewBox.y + viewBox.height / 2;
                  const lx = viewBox.x + viewBox.width + Layout.axisLabelOffset;
                  return (
                    <text x={lx} y={cy} fill={Colors.textPrimary} fontSize={14} textAnchor="middle" dominantBaseline="central" transform={`rotate(90, ${lx}, ${cy})`}>
                      {t('chart.accuracy')}
                    </text>
                  );
                }}
              />
              <Tooltip content={() => null} cursor={{ fill: 'transparent', stroke: 'transparent' }} />
              <Legend content={() => (
                <div className={css.legend}>
                  {hasNonFc && (
                    <span className={css.legendItem}>
                      <span style={{ display: 'inline-block', width: 12, height: 12, borderRadius: 2, background: 'linear-gradient(to right, rgb(220,40,40), rgb(46,204,113))' }} />
                      {t('chart.accuracy')}
                    </span>
                  )}
                  {hasFc && (
                    <span className={css.legendItem}>
                      <span style={{ display: 'inline-block', width: 12, height: 12, borderRadius: 2, backgroundColor: Colors.gold }} />
                      {t('chart.accuracyFC')}
                    </span>
                  )}
                  <span className={css.legendItem}>
                    <svg width={24} height={12} style={{ verticalAlign: 'middle' }}>
                      <line x1={0} y1={6} x2={18} y2={6} stroke={Colors.accentBlueBright} strokeWidth={2} />
                      <circle cx={18} cy={6} r={3} fill={Colors.accentBlueBright} />
                    </svg>
                    {t('chart.score')}
                  </span>
                </div>
              )} />
              <Bar yAxisId="accuracy" dataKey="accuracy" radius={[RAD, RAD, 0, 0]} isAnimationActive={false} shape={CustomBar as any} />
              <Line yAxisId="score" type="monotone" dataKey="score" stroke={Colors.accentBlueBright} strokeWidth={2} dot={{ fill: Colors.accentBlueBright, r: Size.dotRadius }} isAnimationActive={false} />
            </ComposedChart>
          </ResponsiveContainer>
        </div>
      </FadeIn>
    </div>
  );
}
