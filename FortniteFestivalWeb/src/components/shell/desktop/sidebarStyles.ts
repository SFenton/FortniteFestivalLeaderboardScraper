import type { CSSProperties } from 'react';
import {
  Colors, Font, Gap, Weight, Radius, Border, Display, Align, Position, BoxSizing, Cursor, CssValue, CssProp, Layout,
  flexColumn, flexRow, flexCenter, modalOverlay, btnDanger, purpleGlass,
  padding, border, transition, transitions, LINK_TRANSITION_MS,
} from '@festival/theme';

const NOISE_BG = "url(\"data:image/svg+xml,%3Csvg%20xmlns%3D'http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg'%20width%3D'200'%20height%3D'200'%3E%3Cfilter%20id%3D'n'%3E%3CfeTurbulence%20type%3D'fractalNoise'%20baseFrequency%3D'0.75'%20numOctaves%3D'4'%20stitchTiles%3D'stitch'%2F%3E%3CfeColorMatrix%20type%3D'saturate'%20values%3D'0'%2F%3E%3C%2Ffilter%3E%3Crect%20width%3D'100%25'%20height%3D'100%25'%20filter%3D'url(%2523n)'%20opacity%3D'0.4'%2F%3E%3C%2Fsvg%3E\")";

const bgTransition = transitions(
  transition(CssProp.backgroundColor, LINK_TRANSITION_MS),
  transition(CssProp.borderColor, LINK_TRANSITION_MS),
  transition(CssProp.boxShadow, LINK_TRANSITION_MS),
  transition(CssProp.color, LINK_TRANSITION_MS),
);

const sidebarLink: CSSProperties = {
  ...flexRow,
  gap: Gap.lg,
  height: Layout.entryRowHeight,
  padding: padding(0, Gap.xl),
  color: Colors.textPrimary,
  textDecoration: CssValue.none,
  fontSize: Font.lg,
  fontWeight: Weight.semibold,
  boxSizing: BoxSizing.borderBox,
  backgroundColor: CssValue.transparent,
  borderTop: border(Border.thin, CssValue.transparent),
  borderRight: border(Border.thin, CssValue.transparent),
  borderBottom: border(Border.thin, CssValue.transparent),
  borderLeft: border(Border.thin, CssValue.transparent),
  boxShadow: CssValue.none,
  transition: bgTransition,
};

export const sidebarStyles = {
  overlay: { ...modalOverlay, zIndex: 200, backgroundColor: Colors.overlayDark } as CSSProperties,
  sidebar: { position: Position.fixed, top: 0, left: 0, bottom: 0, width: 280, backgroundColor: Colors.backgroundCard, backgroundImage: NOISE_BG, backgroundRepeat: 'repeat', borderRight: `1px solid ${Colors.glassBorder}`, boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.06), inset 0 0 30px rgba(255,255,255,0.02), 0 4px 20px rgba(0,0,0,0.4)', zIndex: 201, ...flexColumn, overflow: 'hidden' } as CSSProperties,
  sidebarHeader: { padding: `${Gap.section}px ${Gap.section}px ${Gap.xl}px`, borderBottom: `1px solid ${Colors.borderSubtle}` } as CSSProperties,
  brand: { fontSize: Font.lg, fontWeight: Weight.bold, color: Colors.accentPurple } as CSSProperties,
  sidebarNav: { ...flexColumn, padding: padding(Gap.md, 0), gap: Gap.xs, flex: 1 } as CSSProperties,
  sidebarFooter: { borderTop: `1px solid ${Colors.borderSubtle}`, ...flexColumn, padding: padding(Gap.md, 0), gap: Gap.xs } as CSSProperties,
  sidebarLink,
  sidebarLinkActive: {
    ...sidebarLink,
    backgroundColor: Colors.accentPurple,
    borderTop: border(Border.thin, Colors.purpleBorderGlass),
    borderRight: border(Border.thin, Colors.purpleBorderGlass),
    borderBottom: border(Border.thin, Colors.purpleBorderGlass),
    borderLeft: `3px solid ${Colors.accentPurple}`,
    boxShadow: purpleGlass.boxShadow,
    color: Colors.textPrimary,
  } as CSSProperties,
  sidebarLinkIcon: { ...flexRow, flexShrink: 0 } as CSSProperties,
  sidebarPlayerRow: { display: Display.flex, alignItems: Align.center } as CSSProperties,
  playerLink: { ...sidebarLink, flex: 1 } as CSSProperties,
  profileCircle: { ...flexCenter, width: 28, height: 28, borderRadius: '50%', backgroundColor: Colors.surfaceSubtle, border: `1px solid ${Colors.borderSubtle}`, flexShrink: 0, marginRight: Gap.md } as CSSProperties,
  profileCircleEmpty: { ...flexCenter, width: 28, height: 28, borderRadius: '50%', backgroundColor: '#D0D5DD', border: 'none', flexShrink: 0, marginRight: Gap.md, color: '#4A5568' } as CSSProperties,
  deselectBtn: { ...btnDanger, padding: `${Gap.sm}px ${Gap.xl}px`, fontSize: Font.sm, fontWeight: Weight.semibold, whiteSpace: 'nowrap', marginRight: Gap.section } as CSSProperties,
  selectPlayerBtn: { ...sidebarLink, background: CssValue.none, cursor: Cursor.pointer } as CSSProperties,
} as const;
