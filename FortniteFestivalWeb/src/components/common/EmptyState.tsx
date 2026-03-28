/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type ReactNode, type CSSProperties } from 'react';
import { Colors, Font, Gap, Weight, Align, Justify, TextAlign, Layout, flexColumn, padding } from '@festival/theme';

export interface EmptyStateProps {
  title: string;
  subtitle?: string;
  icon?: ReactNode;
  /** When true, applies a minHeight so the empty state is vertically centered in the viewport. */
  fullPage?: boolean;
  style?: CSSProperties;
  onAnimationEnd?: (e: React.AnimationEvent<HTMLElement>) => void;
  className?: string;
}

export default function EmptyState({ title, subtitle, icon, fullPage, style, onAnimationEnd, className }: EmptyStateProps) {
  const s = useStyles(fullPage);
  return (
    <div className={className} style={{ ...s.root, ...style }} onAnimationEnd={onAnimationEnd}>
      {icon}
      <div style={s.title}>{title}</div>
      {subtitle && <div style={s.subtitle}>{subtitle}</div>}
    </div>
  );
}

function useStyles(fullPage?: boolean) {
  return useMemo(() => ({
    root: {
      ...flexColumn,
      alignItems: Align.center,
      justifyContent: Justify.center,
      gap: Gap.md,
      padding: padding(48, Gap.xl),
      textAlign: TextAlign.center,
      ...(fullPage ? { minHeight: `calc(100vh - ${Layout.shellChromeHeight}px)` } : undefined),
    },
    title: { fontSize: Font.xl, fontWeight: Weight.bold, color: Colors.textPrimary },
    subtitle: { fontSize: Font.md, color: Colors.textMuted },
  }), []);
}
