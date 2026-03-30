import type { CSSProperties } from 'react';
import {
  Colors, Font, Gap, Radius, Layout, MaxWidth, Border, IconSize,
  Display, Align, Justify, Overflow, CssValue, CssProp, TextAlign,
  Position, BoxSizing, ZIndex, PointerEvents,
  frostedCard, flexRow, flexColumn, padding, border, transition,
  NAV_TRANSITION_MS,
} from '@festival/theme';
import { IS_PWA } from '@festival/ui-utils';

const pwaOffset = IS_PWA ? Gap.section - Gap.md : 0;

const entryBase: CSSProperties = {
  ...frostedCard,
  ...flexRow,
  gap: Gap.xl,
  padding: padding(0, Gap.xl),
  height: Layout.entryRowHeight,
  borderRadius: Radius.md,
  textDecoration: CssValue.none,
  color: CssValue.inherit,
  transition: transition(CssProp.backgroundColor, NAV_TRANSITION_MS),
  fontSize: Font.md,
};

const paginationBase: CSSProperties = {
  ...flexRow,
  flexShrink: 0,
  padding: padding(Gap.md, Layout.paddingHorizontal),
  maxWidth: MaxWidth.card,
  margin: CssValue.marginCenter,
  width: CssValue.full,
  boxSizing: BoxSizing.borderBox,
  position: Position.relative,
  zIndex: 1,
};

const fixedFooterBase: CSSProperties = {
  left: Gap.none,
  right: Gap.none,
  maxWidth: MaxWidth.card,
  margin: CssValue.marginCenter,
  padding: padding(0, Layout.paddingHorizontal),
  boxSizing: BoxSizing.borderBox,
  zIndex: ZIndex.fixedFooter,
  pointerEvents: PointerEvents.auto,
};

/** Wide-desktop total width: 2×sidebar + content + 2×pinned-padding. */
const wideMaxWidth = Layout.sidebarWidth * 2 + MaxWidth.card + Layout.paddingHorizontalPinned * 2;
const wideGutter = `max(${Layout.sidebarWidth}px, calc((100vw - ${wideMaxWidth}px) / 2 + ${Layout.sidebarWidth}px))`;

/**
 * Override left/right for fixed-position footers in wide-desktop mode
 * so they align with the center column instead of the full viewport.
 */
export const fixedFooterWide: CSSProperties = {
  left: wideGutter,
  right: wideGutter,
  maxWidth: CssValue.none as string,
};

/** Shared styles used by PaginatedLeaderboard and its consumers. */
export const plbStyles = {
  /* ── Row list ── */
  list: {
    ...flexColumn,
    gap: Gap.sm,
    overflow: Overflow.hidden,
  } as CSSProperties,

  entryRow: { ...entryBase } as CSSProperties,

  playerEntryRow: {
    ...entryBase,
    backgroundColor: Colors.purpleHighlight,
    border: border(Border.thin, Colors.purpleHighlightBorder),
  } as CSSProperties,

  emptyRow: {
    padding: Gap.xl,
    textAlign: TextAlign.center,
    color: Colors.textMuted,
  } as CSSProperties,

  /* ── Pagination inner styles (content layout, not fixed position) ── */
  pagination: {
    ...paginationBase,
    justifyContent: Justify.center,
    gap: Gap.md,
  } as CSSProperties,

  paginationMobile: {
    ...paginationBase,
    justifyContent: Justify.between,
    gap: Gap.none,
  } as CSSProperties,

  pageInfoBadge: {
    ...frostedCard,
    display: Display.inlineFlex,
    alignItems: Align.center,
    justifyContent: Justify.center,
    height: IconSize.lg,
    fontSize: Font.sm,
    color: Colors.textSecondary,
    padding: padding(0, Gap.xl),
    borderRadius: Radius.sm,
    backgroundColor: Colors.backgroundCard,
    boxSizing: BoxSizing.borderBox,
  } as CSSProperties,

  /* ── Fixed footer positioning (portaled to body) ── */
  mobilePagination: {
    position: Position.fixed,
    bottom: Layout.fabBottom + pwaOffset + (Layout.fabSize - Layout.entryRowHeight) / 2 + Layout.entryRowHeight + Gap.sm,
    ...fixedFooterBase,
  } as CSSProperties,

  mobilePaginationNoPlayer: {
    position: Position.fixed,
    bottom: Layout.fabBottom + pwaOffset + Layout.fabSize + Gap.sm,
    ...fixedFooterBase,
  } as CSSProperties,

  desktopPagination: {
    position: Position.fixed,
    bottom: Layout.entryRowHeight + Gap.xl,
    ...fixedFooterBase,
  } as CSSProperties,

  desktopPaginationNoPlayer: {
    position: Position.fixed,
    bottom: Gap.none,
    ...fixedFooterBase,
  } as CSSProperties,

  playerFooterFab: {
    position: Position.fixed,
    bottom: Layout.fabBottom + pwaOffset + (Layout.fabSize - Layout.entryRowHeight) / 2,
    ...fixedFooterBase,
  } as CSSProperties,

  desktopPlayerFooter: {
    position: Position.fixed,
    bottom: Gap.none,
    ...fixedFooterBase,
  } as CSSProperties,

  playerFooterRow: {
    ...frostedCard,
    ...flexRow,
    gap: Gap.xl,
    height: Layout.entryRowHeight,
    padding: padding(0, Gap.xl),
    borderRadius: Radius.md,
    backgroundColor: Colors.purpleHighlight,
    border: border(Border.thin, Colors.purpleHighlightBorder),
    fontSize: Font.md,
    textDecoration: CssValue.none,
    color: CssValue.inherit,
  } as CSSProperties,
};
