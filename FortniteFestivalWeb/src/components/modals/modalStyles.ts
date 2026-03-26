import type { CSSProperties } from 'react';
import { Colors, Font, Gap, Weight, Radius, ZIndex, Display, Align, Justify, Position, TextAlign, Cursor, Overflow, CssValue, flexColumn, flexCenter, modalOverlay, modalCard, btnPrimary, btnDanger, transition } from '@festival/theme';

/* ── Modal overlay ── */
export const modalStyles = {
  overlay: { ...modalOverlay, zIndex: ZIndex.modalOverlay, transition: 'opacity 300ms ease' } as CSSProperties,

  /* ── Panel (shared base) ── */
  panelBase: { ...modalCard, position: Position.fixed, zIndex: 1001, ...flexColumn } as CSSProperties,
  panelMobile: { ...modalCard, position: Position.fixed, zIndex: 1001, ...flexColumn, left: 0, right: 0, borderTopLeftRadius: Radius.lg, borderTopRightRadius: Radius.lg, transition: 'transform 300ms ease' } as CSSProperties,
  panelDesktop: { ...modalCard, position: Position.fixed, zIndex: 1001, ...flexColumn, top: '50%', left: '50%', width: '80vw', maxWidth: '90vw', height: '70vh', borderRadius: Radius.lg, transition: 'opacity 300ms ease, transform 300ms ease' } as CSSProperties,

  /* ── Header ── */
  headerWrap: { display: Display.flex, alignItems: Align.center, justifyContent: Justify.spaceBetween, padding: `${Gap.xl}px ${Font.lg}px ${Gap.xl}px ${Gap.section}px`, flexShrink: 0 } as CSSProperties,
  headerTitle: { fontSize: Font.xl, fontWeight: Weight.bold, margin: 0 } as CSSProperties,
  closeBtn: { width: 32, height: 32, borderRadius: '50%', background: Colors.surfaceElevated, border: `1px solid ${Colors.borderPrimary}`, color: Colors.textSecondary, ...flexCenter, cursor: Cursor.pointer, flexShrink: 0 } as CSSProperties,

  /* ── Content scroll ── */
  contentScroll: { flex: 1, overflowY: 'auto', padding: `${Gap.xl}px ${Gap.section}px` } as CSSProperties,

  /* ── Footer ── */
  footerWrap: { display: Display.flex, alignItems: Align.center, padding: `${Gap.xl}px ${Gap.section}px`, flexShrink: 0 } as CSSProperties,
  resetBtn: { ...btnDanger, width: CssValue.full, fontSize: Font.md, padding: Gap.xl } as CSSProperties,
  applyBtn: { ...btnPrimary, width: CssValue.full, fontSize: Font.lg, fontWeight: Weight.bold, padding: Gap.xl, transition: 'opacity 300ms ease' } as CSSProperties,
  applyBtnDisabled: { opacity: 0.4, cursor: Cursor.default } as CSSProperties,

  /* ── ModalSection ── */
  sectionWrap: { marginBottom: Gap.section } as CSSProperties,
  sectionTitle: { fontSize: Font.lg, fontWeight: Weight.bold, marginBottom: Gap.sm, color: Colors.textPrimary } as CSSProperties,
  sectionHint: { fontSize: Font.sm, color: Colors.textSecondary, marginBottom: Gap.md, lineHeight: 1.4 } as CSSProperties,

  /* ── RadioRow ── */
  radioRow: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, width: CssValue.full, padding: Gap.xl, backgroundColor: 'transparent', border: 'none', borderRadius: Radius.xs, color: Colors.textSecondary, fontSize: Font.md, fontWeight: Weight.semibold, cursor: Cursor.pointer, marginBottom: Gap.xs, textAlign: TextAlign.left } as CSSProperties,
  radioRowSelected: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, width: CssValue.full, padding: Gap.xl, backgroundColor: 'transparent', border: 'none', borderRadius: Radius.xs, color: Colors.textPrimary, fontSize: Font.md, fontWeight: Weight.semibold, cursor: Cursor.pointer, marginBottom: Gap.xs, textAlign: TextAlign.left } as CSSProperties,
  radioDot: { width: 18, height: 18, borderRadius: '50%', border: `2px solid ${Colors.borderPrimary}`, flexShrink: 0, boxSizing: 'border-box' as const, position: Position.relative, top: 1 } as CSSProperties,
  radioDotSelected: { width: 18, height: 18, borderRadius: '50%', border: `2px solid ${Colors.accentBlue}`, backgroundColor: Colors.accentBlue, boxShadow: `inset 0 0 0 2px ${Colors.surfaceFrosted}`, flexShrink: 0, boxSizing: 'border-box' as const, position: Position.relative, top: 1 } as CSSProperties,

  /* ── ToggleRow ── */
  toggleRow: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, width: CssValue.full, padding: `${Gap.md}px 0`, backgroundColor: 'transparent', border: 'none', borderRadius: 0, cursor: Cursor.pointer, textAlign: TextAlign.left, color: Colors.textPrimary, transition: 'opacity 300ms ease' } as CSSProperties,
  toggleRowLarge: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, width: CssValue.full, padding: `${Gap.lg}px 0`, backgroundColor: 'transparent', border: 'none', borderRadius: 0, cursor: Cursor.pointer, textAlign: TextAlign.left, color: Colors.textPrimary, transition: 'opacity 300ms ease' } as CSSProperties,
  toggleRowDisabled: { opacity: 0.5, cursor: Cursor.default } as CSSProperties,
  toggleIcon: { flexShrink: 0, display: Display.flex, alignItems: Align.center } as CSSProperties,
  toggleContent: { flex: 1 } as CSSProperties,
  toggleLabel: { fontSize: Font.md, fontWeight: Weight.semibold } as CSSProperties,
  toggleLabelLarge: { fontSize: Font.lg } as CSSProperties,
  toggleDesc: { fontSize: Font.sm, color: Colors.textMuted, marginTop: Gap.xs } as CSSProperties,
  toggleDescLarge: { fontSize: Font.md } as CSSProperties,
  toggleTrack: { width: 36, height: 20, borderRadius: 10, backgroundColor: Colors.surfaceMuted, position: Position.relative, flexShrink: 0, transition: 'background-color 0.15s' } as CSSProperties,
  toggleTrackOn: { backgroundColor: Colors.accentBlue } as CSSProperties,
  toggleTrackDisabled: { opacity: 0.4 } as CSSProperties,
  toggleThumb: { width: 16, height: 16, borderRadius: '50%', backgroundColor: Colors.textPrimary, position: Position.absolute, top: 2, left: 2, transition: 'left 0.15s' } as CSSProperties,
  toggleThumbOn: { left: 18 } as CSSProperties,
  toggleTrackLarge: { width: 44, height: 24, borderRadius: 12, backgroundColor: Colors.surfaceMuted, position: Position.relative, flexShrink: 0, transition: 'background-color 0.15s' } as CSSProperties,
  toggleTrackLargeOn: { width: 44, height: 24, borderRadius: 12, backgroundColor: Colors.accentBlue, position: Position.relative, flexShrink: 0, transition: 'background-color 0.15s' } as CSSProperties,
  toggleThumbLarge: { width: 20, height: 20, borderRadius: '50%', backgroundColor: Colors.textPrimary, position: Position.absolute, top: 2, left: 2, transition: 'left 0.15s' } as CSSProperties,
  toggleThumbLargeOn: { left: 22 } as CSSProperties,

  /* ── ReorderList ── */
  reorderList: { ...flexColumn, border: `1px solid ${Colors.borderSubtle}`, borderRadius: Radius.xs, overflow: Overflow.hidden } as CSSProperties,
  reorderRow: { display: Display.flex, alignItems: Align.center, gap: Gap.xl, padding: Gap.xl, backgroundColor: Colors.surfaceSubtle, borderBottom: `1px solid ${Colors.borderSubtle}`, touchAction: 'none' } as CSSProperties,
  dragHandle: { color: Colors.textMuted, fontSize: Font.lg, flexShrink: 0, userSelect: 'none' as const, lineHeight: 1 } as CSSProperties,
  reorderLabel: { flex: 1, fontSize: Font.md, fontWeight: Weight.semibold, color: Colors.textPrimary } as CSSProperties,

  /* ── Accordion ── */
  accordionHeader: { display: Display.flex, alignItems: Align.center, gap: Gap.md, width: CssValue.full, padding: `${Gap.md}px 0`, background: 'none', border: 'none', cursor: Cursor.pointer, color: Colors.textPrimary, textAlign: TextAlign.left } as CSSProperties,
  accordionTitleGroup: { ...flexColumn, gap: Gap.xs, flex: 1 } as CSSProperties,
  accordionIcon: { ...flexCenter, flexShrink: 0 } as CSSProperties,
  accordionTitle: { fontSize: Font.lg, fontWeight: Weight.bold } as CSSProperties,
  accordionHint: { fontSize: Font.sm, color: Colors.textSecondary } as CSSProperties,
  accordionChevron: { flexShrink: 0, transition: 'transform 0.2s ease', color: Colors.textMuted } as CSSProperties,
  accordionBodyWrap: { display: 'grid', transition: 'grid-template-rows 0.2s ease' } as CSSProperties,
  accordionBodyInner: { overflow: Overflow.hidden, minHeight: 0, paddingLeft: Gap.xl } as CSSProperties,

  /* ── BulkActions ── */
  bulkWrap: { display: Display.flex, justifyContent: Justify.flexEnd, gap: Gap.md, marginBottom: Gap.xl } as CSSProperties,
  bulkSelectBtn: { ...btnPrimary, padding: `${Gap.sm}px ${Gap.md}px`, fontSize: Font.sm } as CSSProperties,
  bulkClearBtn: { ...btnDanger, padding: `${Gap.sm}px ${Gap.md}px`, fontSize: Font.sm } as CSSProperties,
} as const;
