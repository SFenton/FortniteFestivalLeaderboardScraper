/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * Shared metric-info FRE slide component.
 * Renders a title, body paragraphs, and optional LaTeX formulas.
 */
import { useMemo, type CSSProperties } from 'react';
import FadeIn from '../../../../components/page/FadeIn';
import Math from '../../../../components/common/Math';
import {
  Colors, Font, Weight, Gap, LineHeight,
  Display, CssValue, PointerEvents, flexColumn,
} from '@festival/theme';

export interface MetricInfoSlideProps {
  paragraphs: string[];
  formulas?: string[];
}

export default function MetricInfoSlide({ paragraphs, formulas }: MetricInfoSlideProps) {
  const s = useStyles();

  return (
    <div style={s.wrapper}>
      {paragraphs.map((p, i) => (
        <FadeIn key={i} delay={i * 80} style={s.para}>
          {p}
        </FadeIn>
      ))}
      {formulas && formulas.map((f, i) => (
        <FadeIn key={`f${i}`} delay={(paragraphs.length + i) * 80} style={s.formula}>
          <Math tex={f} block />
        </FadeIn>
      ))}
    </div>
  );
}

function useStyles() {
  return useMemo(() => ({
    wrapper: {
      ...flexColumn,
      gap: Gap.lg,
      width: CssValue.full,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    para: {
      fontSize: Font.md,
      color: Colors.textSecondary,
      lineHeight: LineHeight.relaxed,
    } as CSSProperties,
    formula: {
      display: Display.flex,
      justifyContent: 'center' as const,
      padding: `${Gap.md}px 0`,
      color: Colors.textPrimary,
      fontSize: Font.lg,
      fontWeight: Weight.normal,
    } as CSSProperties,
  }), []);
}
