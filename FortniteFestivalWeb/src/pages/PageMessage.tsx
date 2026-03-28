/**
 * Centered page-level message for empty states.
 */
import { useMemo, type ReactNode, type CSSProperties } from 'react';
import { Colors, Font, Gap, Layout, flexCenter, padding } from '@festival/theme';

export interface PageMessageProps {
  children: ReactNode;
}

export function PageMessage({ children }: PageMessageProps) {
  const s = useStyles();
  return <div style={s.message}>{children}</div>;
}

function useStyles() {
  return useMemo(() => ({
    message: {
      ...flexCenter,
      padding: padding(Gap.section, Gap.none),
      color: Colors.textSecondary,
      fontSize: Font.lg,
      minHeight: Layout.pageMessageMinHeight,
    } as CSSProperties,
  }), []);
}
