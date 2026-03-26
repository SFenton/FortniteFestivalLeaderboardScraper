import type { CSSProperties } from 'react';
import { Layout, MaxWidth, Gap, ZIndex, Overflow, flexColumn, flexRow, CssValue, Position, Display, Align, BoxSizing, PointerEvents } from '@festival/theme';

/** Computed max-width for the wide-desktop layout (2×sidebar + content + 2×padding). */
const wideMaxWidth = Layout.sidebarWidth * 2 + MaxWidth.card + Layout.paddingHorizontalPinned * 2;

export const appStyles = {
  shell: { ...flexColumn, height: '100dvh', overflow: Overflow.hidden } as CSSProperties,
  /**
   * Wide-desktop body section: positioned container for overlays + scroll.
   * The scroll container is absolute-fill for native scroll everywhere.
   */
  bodySection: { flex: 1, position: Position.relative, minHeight: 0 } as CSSProperties,
  /** Full-area scroll container. */
  scrollContainerFull: { position: Position.absolute, top: 0, left: 0, right: 0, bottom: 0, overflowY: Overflow.auto } as CSSProperties,
  /** Row inside scroll that centers content and reserves sidebar gutters. */
  scrollContentRow: { ...flexRow, alignItems: 'stretch' as const, maxWidth: wideMaxWidth, margin: CssValue.marginCenter, width: CssValue.full } as CSSProperties,
  /** Transparent gutter reserving space under the sidebar overlay. */
  sidebarGutter: { width: Layout.sidebarWidth, flexShrink: 0 } as CSSProperties,
  /** Center column inside scroll (header spacer + content). */
  centerColumn: { flex: 1, ...flexColumn } as CSSProperties,
  /** Right gutter for symmetry. */
  rightGutter: { width: Layout.sidebarWidth, flexShrink: 0 } as CSSProperties,
  /** Sidebar overlay — absolutely positioned, centered. pointer-events: none lets wheel through. */
  sidebarOverlay: { position: Position.absolute, top: 0, bottom: 0, left: 0, right: 0, maxWidth: wideMaxWidth, margin: CssValue.marginCenter, pointerEvents: PointerEvents.none, zIndex: ZIndex.overlay } as CSSProperties,
  /** Header overlay — absolutely positioned at top, centered. pointer-events: none lets wheel through. */
  headerOverlay: { position: Position.absolute, top: 0, left: 0, right: 0, maxWidth: wideMaxWidth, margin: CssValue.marginCenter, pointerEvents: PointerEvents.none, zIndex: ZIndex.dropdown, display: Display.flex } as CSSProperties,
  /** Portal target inside header overlay — pointer-events restored for interaction. */
  headerPortalWide: { flex: 1, pointerEvents: PointerEvents.auto } as CSSProperties,
  /** Non-wide: simple flex column layout. */
  scrollContainer: { flex: 1, ...flexColumn, overflowY: Overflow.auto, minHeight: 0 } as CSSProperties,
  contentColumn: { flex: 1, ...flexColumn, minHeight: 0 } as CSSProperties,
  headerPortal: { flexShrink: 0 } as CSSProperties,
  rightSpacer: { width: Layout.sidebarWidth, flexShrink: 0 } as CSSProperties,
  nav: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px ${Gap.md}px`, maxWidth: MaxWidth.card, margin: CssValue.marginCenter, width: CssValue.full, boxSizing: BoxSizing.borderBox, backgroundColor: 'transparent', flexShrink: 0, zIndex: ZIndex.popover } as CSSProperties,
  navWide: { gap: 0, maxWidth: wideMaxWidth, paddingLeft: 0, paddingRight: 0, padding: `${Layout.paddingTop}px 0 ${Gap.md}px` } as CSSProperties,
  sidebarSpacer: { width: Layout.sidebarWidth, flexShrink: 0 } as CSSProperties,
  navWideInner: { flex: 1, display: Display.flex, alignItems: Align.center, gap: Gap.xl, maxWidth: MaxWidth.card, margin: CssValue.marginCenter, padding: `0 ${Layout.paddingHorizontal}px`, boxSizing: BoxSizing.borderBox } as CSSProperties,
  content: { flex: 1, ...flexColumn, position: Position.relative } as CSSProperties,
  contentPinned: { '--layout-padding-h': `${Layout.paddingHorizontalPinned}px` } as CSSProperties,
  spacer: { flex: 1 } as CSSProperties,
} as const;
