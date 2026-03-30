import type { CSSProperties } from 'react';
import {
  Colors, Font, Gap, Weight, Radius, Layout, ZIndex, Border, Opacity, LineHeight,
  Display, Align, Justify, Position, TextAlign, Cursor, Overflow, BoxSizing, CssValue,
  flexColumn, flexCenter, modalOverlay, modalCard, btnPrimary, btnDanger,
  border, padding, transition, CssProp, TRANSITION_MS, QUICK_FADE_MS,
} from '@festival/theme';

const panelZ = Layout.modalPanelZ;
const modalTransition = transition(CssProp.opacity, TRANSITION_MS);
const transformTransition = transition(CssProp.transform, TRANSITION_MS);

/* ── Modal overlay ── */
export const modalStyles = {
  overlay: { ...modalOverlay, zIndex: ZIndex.modalOverlay, transition: modalTransition } as CSSProperties,

  /* ── Panel (shared base) ── */
  panelBase: { ...modalCard, position: Position.fixed, zIndex: panelZ, ...flexColumn } as CSSProperties,
  panelMobile: { ...modalCard, position: Position.fixed, zIndex: panelZ, ...flexColumn, left: Gap.none, right: Gap.none, borderTopLeftRadius: Radius.lg, borderTopRightRadius: Radius.lg, overflow: Overflow.hidden, transition: transformTransition } as CSSProperties,
  panelDesktop: { ...modalCard, position: Position.fixed, zIndex: panelZ, ...flexColumn, top: CssValue.circle, left: CssValue.circle, width: '80vw', maxWidth: '90vw', height: '70vh', borderRadius: Radius.lg, transition: `${modalTransition}, ${transformTransition}` } as CSSProperties,

  /* ── Header ── */
  headerWrap: { display: Display.flex, alignItems: Align.center, justifyContent: Justify.between, padding: padding(Gap.xl, Font.lg, Gap.xl, Gap.section), flexShrink: 0 } as CSSProperties,
  headerTitle: { fontSize: Font.xl, fontWeight: Weight.bold, margin: Gap.none } as CSSProperties,
  closeBtn: { width: Layout.modalCloseSize, height: Layout.modalCloseSize, borderRadius: CssValue.circle, background: Colors.surfaceElevated, border: border(Border.thin, Colors.borderPrimary), color: Colors.textSecondary, ...flexCenter, cursor: Cursor.pointer, flexShrink: 0 } as CSSProperties,

  /* ── Content scroll ── */
  contentScroll: { flex: 1, minHeight: 0, overflowY: Overflow.auto, padding: padding(Gap.xl, Gap.section) } as CSSProperties,

  /* ── Footer ── */
  footerWrap: { display: Display.flex, flexDirection: 'column' as const, gap: Gap.md, padding: padding(Gap.xl, Gap.section), flexShrink: 0 } as CSSProperties,
  resetWrap: { marginTop: Gap.section } as CSSProperties,
  resetTitle: { fontSize: Font.lg, fontWeight: Weight.bold, marginBottom: Gap.sm, color: Colors.textPrimary } as CSSProperties,
  resetDesc: { fontSize: Font.sm, color: Colors.textSecondary, marginBottom: Gap.md, lineHeight: LineHeight.snug } as CSSProperties,
  resetBtn: { ...btnDanger, width: CssValue.full, fontSize: Font.md, padding: Gap.xl } as CSSProperties,
  applyBtn: { ...btnPrimary, width: CssValue.full, fontSize: Font.lg, fontWeight: Weight.bold, padding: Gap.xl, transition: modalTransition } as CSSProperties,
  applyBtnDisabled: { opacity: Opacity.faded, cursor: Cursor.default } as CSSProperties,

  /* ── ModalSection ── */
  sectionWrap: { marginBottom: Gap.section } as CSSProperties,
  sectionTitle: { fontSize: Font.lg, fontWeight: Weight.bold, marginBottom: Gap.sm, color: Colors.textPrimary } as CSSProperties,
  sectionHint: { fontSize: Font.sm, color: Colors.textSecondary, marginBottom: Gap.md, lineHeight: 1.4 } as CSSProperties,

  /* ── RadioRow ── */
  radioRow: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, width: CssValue.full, padding: Gap.xl, backgroundColor: CssValue.transparent, border: CssValue.none, borderRadius: Radius.xs, color: Colors.textSecondary, fontSize: Font.md, fontWeight: Weight.semibold, cursor: Cursor.pointer, marginBottom: Gap.xs, textAlign: TextAlign.left } as CSSProperties,
  radioRowSelected: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, width: CssValue.full, padding: Gap.xl, backgroundColor: CssValue.transparent, border: CssValue.none, borderRadius: Radius.xs, color: Colors.textPrimary, fontSize: Font.md, fontWeight: Weight.semibold, cursor: Cursor.pointer, marginBottom: Gap.xs, textAlign: TextAlign.left } as CSSProperties,
  radioDot: { width: Layout.radioDotSize, height: Layout.radioDotSize, borderRadius: CssValue.circle, border: border(Border.thick, Colors.borderPrimary), flexShrink: 0, boxSizing: BoxSizing.borderBox, position: Position.relative, top: Border.thin } as CSSProperties,
  radioDotSelected: { width: Layout.radioDotSize, height: Layout.radioDotSize, borderRadius: CssValue.circle, border: border(Border.thick, Colors.accentBlue), backgroundColor: Colors.accentBlue, boxShadow: `inset 0 0 0 ${Border.thick}px ${Colors.surfaceFrosted}`, flexShrink: 0, boxSizing: BoxSizing.borderBox, position: Position.relative, top: Border.thin } as CSSProperties,
  radioLabelGroup: { display: Display.flex, flexDirection: 'column' as const, gap: Gap.xs, alignItems: Align.start } as CSSProperties,
  radioRowHint: { fontSize: Font.sm, color: Colors.textTertiary, fontWeight: Weight.normal, lineHeight: 1.3 } as CSSProperties,
  radioInfoBtn: { display: Display.flex, alignItems: Align.center, justifyContent: Justify.center, width: 28, height: 28, borderRadius: CssValue.circle, backgroundColor: Colors.surfaceMuted, color: Colors.textSecondary, border: CssValue.none, cursor: Cursor.pointer, flexShrink: 0, marginLeft: 'auto', padding: 0 } as CSSProperties,

  /* ── ToggleRow ── */
  toggleRow: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, width: CssValue.full, padding: padding(Gap.md, 0), backgroundColor: CssValue.transparent, border: CssValue.none, borderRadius: Gap.none, cursor: Cursor.pointer, textAlign: TextAlign.left, color: Colors.textPrimary, transition: modalTransition } as CSSProperties,
  toggleRowSmallerGap: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, width: CssValue.full, padding: padding(Gap.sm, 0), backgroundColor: CssValue.transparent, border: CssValue.none, borderRadius: Gap.none, cursor: Cursor.pointer, textAlign: TextAlign.left, color: Colors.textPrimary, transition: modalTransition } as CSSProperties,
  toggleRowLarge: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, width: CssValue.full, padding: padding(Gap.lg, 0), backgroundColor: CssValue.transparent, border: CssValue.none, borderRadius: Gap.none, cursor: Cursor.pointer, textAlign: TextAlign.left, color: Colors.textPrimary, transition: modalTransition } as CSSProperties,
  toggleRowDisabled: { opacity: Opacity.disabled, cursor: Cursor.default } as CSSProperties,
  toggleIcon: { flexShrink: 0, display: Display.flex, alignItems: Align.center } as CSSProperties,
  toggleContent: { flex: 1 } as CSSProperties,
  toggleLabel: { fontSize: Font.md, fontWeight: Weight.semibold } as CSSProperties,
  toggleLabelLarge: { fontSize: Font.lg } as CSSProperties,
  toggleDesc: { fontSize: Font.sm, color: Colors.textMuted, marginTop: Gap.xs } as CSSProperties,
  toggleDescLarge: { fontSize: Font.md } as CSSProperties,
  toggleTrack: { width: Layout.toggleTrackWidth, height: Layout.toggleTrackHeight, borderRadius: Layout.toggleTrackRadius, backgroundColor: Colors.surfaceMuted, position: Position.relative, flexShrink: 0, transition: transition(CssProp.backgroundColor, QUICK_FADE_MS) } as CSSProperties,
  toggleTrackOn: { backgroundColor: Colors.accentBlue } as CSSProperties,
  toggleTrackDisabled: { opacity: Opacity.faded } as CSSProperties,
  toggleThumb: { width: Layout.toggleThumbSize, height: Layout.toggleThumbSize, borderRadius: CssValue.circle, backgroundColor: Colors.textPrimary, position: Position.absolute, top: Layout.toggleThumbInset, left: Layout.toggleThumbInset, transition: transition(CssProp.left, QUICK_FADE_MS) } as CSSProperties,
  toggleThumbOn: { left: Layout.toggleThumbOnOffset } as CSSProperties,
  toggleTrackLarge: { width: Layout.toggleTrackLargeWidth, height: Layout.toggleTrackLargeHeight, borderRadius: Layout.toggleTrackLargeRadius, backgroundColor: Colors.surfaceMuted, position: Position.relative, flexShrink: 0, transition: transition(CssProp.backgroundColor, QUICK_FADE_MS) } as CSSProperties,
  toggleTrackLargeOn: { width: Layout.toggleTrackLargeWidth, height: Layout.toggleTrackLargeHeight, borderRadius: Layout.toggleTrackLargeRadius, backgroundColor: Colors.accentBlue, position: Position.relative, flexShrink: 0, transition: transition(CssProp.backgroundColor, QUICK_FADE_MS) } as CSSProperties,
  toggleThumbLarge: { width: Layout.toggleThumbLargeSize, height: Layout.toggleThumbLargeSize, borderRadius: CssValue.circle, backgroundColor: Colors.textPrimary, position: Position.absolute, top: Layout.toggleThumbInset, left: Layout.toggleThumbInset, transition: transition(CssProp.left, QUICK_FADE_MS) } as CSSProperties,
  toggleThumbLargeOn: { left: Layout.toggleThumbLargeOnOffset } as CSSProperties,

  /* ── ReorderList ── */
  reorderList: { ...flexColumn, border: border(Border.thin, Colors.borderSubtle), borderRadius: Radius.xs, overflow: Overflow.hidden } as CSSProperties,
  reorderRow: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, padding: Gap.xl, backgroundColor: Colors.surfaceSubtle, borderBottom: border(Border.thin, Colors.borderSubtle), touchAction: CssValue.none } as CSSProperties,
  dragHandle: { color: Colors.textMuted, fontSize: Font.lg, flexShrink: 0, userSelect: CssValue.none as CSSProperties['userSelect'], lineHeight: 1 } as CSSProperties,
  reorderLabel: { flex: 1, fontSize: Font.md, fontWeight: Weight.semibold, color: Colors.textPrimary } as CSSProperties,

  /* ── Accordion ── */
  accordionHeader: { display: Display.flex, alignItems: Align.center, gap: Gap.md, width: CssValue.full, padding: padding(Gap.md, 0), background: CssValue.none, border: CssValue.none, cursor: Cursor.pointer, color: Colors.textPrimary, textAlign: TextAlign.left } as CSSProperties,
  accordionTitleGroup: { ...flexColumn, gap: Gap.xs, flex: 1 } as CSSProperties,
  accordionIcon: { ...flexCenter, flexShrink: 0 } as CSSProperties,
  accordionTitle: { fontSize: Font.lg, fontWeight: Weight.bold } as CSSProperties,
  accordionHint: { fontSize: Font.sm, color: Colors.textSecondary } as CSSProperties,
  accordionChevron: { flexShrink: 0, transition: transition(CssProp.transform, QUICK_FADE_MS), color: Colors.textMuted } as CSSProperties,
  accordionBodyWrap: { display: 'grid' as const, transition: transition('grid-template-rows' as any, QUICK_FADE_MS) } as CSSProperties,
  accordionBodyInner: { overflow: Overflow.hidden, minHeight: 0, paddingLeft: Gap.xl } as CSSProperties,

  /* ── BulkActions ── */
  bulkWrap: { display: Display.flex, justifyContent: Justify.end, gap: Gap.md, marginBottom: Gap.xl } as CSSProperties,
  bulkSelectBtn: { ...btnPrimary, padding: padding(Gap.sm, Gap.md), fontSize: Font.sm } as CSSProperties,
  bulkClearBtn: { ...btnDanger, padding: padding(Gap.sm, Gap.md), fontSize: Font.sm } as CSSProperties,
} as const;
