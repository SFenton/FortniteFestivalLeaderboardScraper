/* eslint-disable react/forbid-dom-props -- dynamic styles required for Recharts custom elements */
import { useState, useEffect, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { ResponsiveContainer, ComposedChart, Bar, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend } from 'recharts';
import { Colors, Size, Layout, Gap } from '@festival/theme';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useChartDimensions } from '../../../../hooks/chart/useChartDimensions';
import { LeaderboardEntry } from '../../../leaderboard/global/components/LeaderboardEntry';
import FadeIn from '../../../../components/page/FadeIn';
import { useChartDemoStyles } from './ChartDemo';

const CYCLE_MS = 2500;
const CARD_HEIGHT = 44;
const RAD = 4;

type ChartPoint = { dateLabel: string; cardLabel: string; score: number; accuracy: number; displayAcc: number; isFullCombo: boolean };

function buildDemoData(): ChartPoint[] {
  const today = new Date();
  const fmt = (d: Date) => `${d.getMonth() + 1}/${d.getDate()}/${String(d.getFullYear()).slice(-2)}`;
  const cardFmt = (d: Date) => d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
  const daysAgo = (n: number) => { const d = new Date(today); d.setDate(d.getDate() - n); return d; };
  return [
    { dateLabel: fmt(daysAgo(2)), cardLabel: cardFmt(daysAgo(2)), score: 218400, accuracy: 62, displayAcc: 620000, isFullCombo: false },
    { dateLabel: fmt(daysAgo(1)), cardLabel: cardFmt(daysAgo(1)), score: 347100, accuracy: 78, displayAcc: 780000, isFullCombo: false },
    { dateLabel: fmt(today),      cardLabel: cardFmt(today),      score: 486500, accuracy: 100, displayAcc: 1000000, isFullCombo: true },
  ];
}
const DATA: ChartPoint[] = buildDemoData();

function barFill(accuracy: number, isFC: boolean): string {
  if (accuracy >= 100 && isFC) return Colors.gold;
  const t = Math.min(Math.max(accuracy / 100, 0), 1);
  const r = Math.round(220 * (1 - t) + 46 * t);
  const g = Math.round(40 * (1 - t) + 204 * t);
  const b = Math.round(40 * (1 - t) + 113 * t);
  return `rgb(${r},${g},${b})`;
}

export default function BarSelectDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();
  const [selectedIdx, setSelectedIdx] = useState(0);
  const [detailVisible, setDetailVisible] = useState(true);
  const intervalRef = useRef<ReturnType<typeof setInterval>>(undefined);
  const chartContainerRef = useRef<HTMLDivElement>(null);
  const { maxBars } = useChartDimensions(chartContainerRef);

  const visibleData = useMemo(
    () => DATA.slice(Math.max(0, DATA.length - maxBars)),
    [maxBars],
  );

  const chartHeight = h ? Math.max(80, h - CARD_HEIGHT - 24) : 160;

  const hasFc = visibleData.some(p => p.accuracy >= 100 && p.isFullCombo);
  /* v8 ignore start */
  const hasNonFc = visibleData.some(p => !(p.accuracy >= 100 && p.isFullCombo));
  /* v8 ignore stop */

  // Clamp selectedIdx when visible bars shrink
  useEffect(() => {
    /* v8 ignore start */
    setSelectedIdx((prev) => (prev >= visibleData.length ? 0 : prev));
    /* v8 ignore stop */
  }, [visibleData.length]);

  useEffect(() => {
    if (visibleData.length <= 1) {
      setDetailVisible(true);
      return;
    }
    intervalRef.current = setInterval(() => {
      setDetailVisible(false);
      setTimeout(() => {
        setSelectedIdx((prev) => (prev + 1) % visibleData.length);
        setDetailVisible(true);
      }, 300);
    }, CYCLE_MS);
    return () => clearInterval(intervalRef.current);
  }, [visibleData.length]);

  const point = visibleData[selectedIdx]!;

  // Custom bar shape with selection stroke
  const BarShape = useMemo(() => {
    return function Shape(props: { x: number; y: number; width: number; height: number; index: number; payload: ChartPoint }) {
      const { x, y, width: w, height: ht, index, payload } = props;
      /* v8 ignore start */
      if (!ht || ht <= 0) return null;
      /* v8 ignore stop */
      const fill = barFill(payload.accuracy, payload.isFullCombo);
      const isSelected = index === selectedIdx;
      const path = `M${x + RAD},${y + ht} Q${x},${y + ht} ${x},${y + ht - RAD} L${x},${y + RAD} Q${x},${y} ${x + RAD},${y} L${x + w - RAD},${y} Q${x + w},${y} ${x + w},${y + RAD} L${x + w},${y + ht - RAD} Q${x + w},${y + ht} ${x + w - RAD},${y + ht} Z`;
      return (
        <path
          d={path}
          fill={fill}
          fillOpacity={1}
          stroke={isSelected ? Colors.accentPurple : 'transparent'}
          strokeWidth={Size.barSelectionStroke}
        />
      );
    };
  }, [selectedIdx]);

  const s = useChartDemoStyles();

  return (
    <div style={s.wrapper}>
      <FadeIn delay={0}>
        <div style={s.chartCardNoEvents} ref={chartContainerRef}>
          <ResponsiveContainer width="100%" height={chartHeight}>
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
                <div style={s.legend}>
                  {hasNonFc && (
                    <span style={s.legendItem}>
                      <span style={{ display: 'inline-block', width: 12, height: 12, borderRadius: 2, background: 'linear-gradient(to right, rgb(220,40,40), rgb(46,204,113))' }} />
                      {t('chart.accuracy')}
                    </span>
                  )}
                  {hasFc && (
                    <span style={s.legendItem}>
                      <span style={{ display: 'inline-block', width: 12, height: 12, borderRadius: 2, backgroundColor: Colors.gold }} />
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
              <Bar yAxisId="accuracy" dataKey="accuracy" radius={[RAD, RAD, 0, 0]} isAnimationActive={false} shape={BarShape as any} />
              <Line yAxisId="score" type="monotone" dataKey="score" stroke={Colors.accentBlueBright} strokeWidth={2} dot={{ fill: Colors.accentBlueBright, r: Size.dotRadius }} isAnimationActive={false} />
            </ComposedChart>
          </ResponsiveContainer>
          <div style={{ ...s.detailCardBorderless, opacity: detailVisible ? 1 : 0, marginTop: Gap.xl }}>
            <LeaderboardEntry
              label={visibleData[selectedIdx]!.cardLabel}
              displayName=""
              score={point.score}
              accuracy={point.displayAcc}
              isFullCombo={point.isFullCombo}
              showAccuracy
              scoreWidth="7ch"
            />
          </div>
        </div>
      </FadeIn>
    </div>
  );
}
