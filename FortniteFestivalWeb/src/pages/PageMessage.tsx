/**
 * Centered page-level message for empty states and errors.
 * Replaces the repeated `<div className={s.center}>` / `<div className={s.centerError}>` pattern.
 */
import { useMemo, type ReactNode, type CSSProperties } from 'react';
import { Colors, Font, Gap, Layout, flexCenter, padding } from '@festival/theme';

export interface PageMessageProps {
  children: ReactNode;
  /** Render in error style (red text). */
  error?: boolean;
}

export function PageMessage({ children, error }: PageMessageProps) {
  const s = useStyles(error);
  return <div style={s.message}>{children}</div>;
}

function useStyles(error?: boolean) {
  return useMemo(() => ({
    message: {
      ...flexCenter,
      padding: padding(Gap.section, Gap.none),
      color: error ? Colors.statusRed : Colors.textSecondary,
      fontSize: Font.lg,
      minHeight: Layout.pageMessageMinHeight,
    } as CSSProperties,
  }), [error]);
}
