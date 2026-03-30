/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import {
  Gap, Colors, Font, Weight, Radius, Layout, Position, ZIndex,
  Display, Align, Justify, Cursor, WhiteSpace, InstrumentSize,
  flexColumn, flexCenter, flexRow, padding, transition, frostedCard,
  CssProp, FAST_FADE_MS, NAV_TRANSITION_MS,
} from '@festival/theme';

/**
 * Shared styles used across rivals pages (RivalsPage, RivalDetailPage, RivalryPage).
 * Extracted to avoid duplication across pages that share the same visual pattern.
 */
export function useRivalsSharedStyles() {
  return useMemo(() => ({
    container: {
      position: Position.relative,
      zIndex: ZIndex.base,
      paddingBottom: Gap.section,
      ...flexColumn,
      gap: Gap.section,
    } as CSSProperties,
    section: {
      ...flexColumn,
    } as CSSProperties,
    sectionHeader: {
      ...flexRow,
      gap: Gap.md,
      paddingBottom: Gap.md,
    } as CSSProperties,
    sectionHeaderClickable: {
      ...flexRow,
      gap: Gap.md,
      minHeight: InstrumentSize.sm,
      paddingBottom: Gap.md,
      cursor: Cursor.pointer,
      borderRadius: Radius.sm,
      transition: transition(CssProp.opacity, NAV_TRANSITION_MS),
    } as CSSProperties,
    cardHeaderText: {
      flex: 1,
      minWidth: 0,
    } as CSSProperties,
    cardTitle: {
      display: Display.block,
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
      marginBottom: Gap.xs,
    } as CSSProperties,
    cardDesc: {
      display: Display.block,
      fontSize: Font.sm,
      color: Colors.textPrimary,
    } as CSSProperties,
    seeAll: {
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
      flexShrink: 0,
      whiteSpace: WhiteSpace.nowrap,
      padding: 0,
      margin: 0,
    } as CSSProperties,
    chevron: {
      color: Colors.textPrimary,
      flexShrink: 0,
      padding: 0,
      margin: 0,
    } as CSSProperties,
    rivalList: {
      ...flexColumn,
      gap: 2,
      containerType: 'inline-size',
    } as CSSProperties,
    songList: {
      ...flexColumn,
      gap: 2,
    } as CSSProperties,
    center: {
      ...flexCenter,
      padding: padding(Layout.pillButtonHeight, Gap.none),
      color: Colors.textSecondary,
      fontSize: Font.lg,
    } as CSSProperties,
    viewAllButton: {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      cursor: Cursor.pointer,
      transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
    } as CSSProperties,
  }), []);
}
