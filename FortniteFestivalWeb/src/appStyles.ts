import type { CSSProperties } from 'react';
import { Layout, MaxWidth, Gap, Colors, Font, Weight, ZIndex, flexColumn, flexRow, flexCenter, CssValue, Position, Display, Align, BoxSizing } from '@festival/theme';

/** Computed max-width for the wide-desktop layout (2×sidebar + content + 2×padding). */
const wideMaxWidth = Layout.sidebarWidth * 2 + MaxWidth.card + Layout.paddingHorizontalPinned * 2;

export const appStyles = {
  shell: { ...flexColumn, minHeight: '100dvh' } as CSSProperties,
  contentRow: { flex: 1, ...flexRow, maxWidth: wideMaxWidth, margin: CssValue.marginCenter, width: CssValue.full, minHeight: 0 } as CSSProperties,
  contentColumn: { flex: 1, ...flexColumn, minHeight: 0 } as CSSProperties,
  rightSpacer: { width: Layout.sidebarWidth, flexShrink: 0 } as CSSProperties,
  nav: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px ${Gap.md}px`, maxWidth: MaxWidth.card, margin: CssValue.marginCenter, width: CssValue.full, boxSizing: BoxSizing.borderBox, backgroundColor: 'transparent', flexShrink: 0, zIndex: ZIndex.popover, position: Position.sticky, top: 0, touchAction: 'none' } as CSSProperties,
  navWide: { gap: 0, maxWidth: wideMaxWidth, paddingLeft: 0, paddingRight: 0, padding: `${Layout.paddingTop}px 0 ${Gap.md}px` } as CSSProperties,
  sidebarSpacer: { width: Layout.sidebarWidth, flexShrink: 0 } as CSSProperties,
  navWideInner: { flex: 1, display: Display.flex, alignItems: Align.center, gap: Gap.xl, maxWidth: MaxWidth.card, margin: CssValue.marginCenter, padding: `0 ${Layout.paddingHorizontalPinned}px`, boxSizing: BoxSizing.borderBox } as CSSProperties,
  content: { flex: 1, position: Position.relative, minHeight: 0 } as CSSProperties,
  /** Applied when wide desktop is active — sets CSS variable for cascading to child pages. */
  contentPinned: { '--layout-padding-h': `${Layout.paddingHorizontalPinned}px` } as CSSProperties,
  spacer: { flex: 1 } as CSSProperties,
} as const;
