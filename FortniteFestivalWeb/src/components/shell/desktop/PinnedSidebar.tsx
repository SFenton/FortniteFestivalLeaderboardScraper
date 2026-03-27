/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { NavLink, Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoPerson, IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings, IoBagHandle, IoPeople } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import { useSettings } from '../../../contexts/SettingsContext';
import {
  Colors, Font, Weight, Gap, Radius, Border, Layout, ZIndex,
  Display, Align, Justify, Position, Cursor, BoxSizing, CssValue, CssProp,
  flexColumn, flexRow, flexCenter, purpleGlass, transition, transitions, padding, border, margin,
  Overflow, FAST_FADE_MS, LINK_TRANSITION_MS, PointerEvents,
} from '@festival/theme';

interface PinnedSidebarProps {
  player: TrackedPlayer | null;
  onDeselect: () => void;
  onSelectPlayer: () => void;
}

export default function PinnedSidebar({ player, onDeselect, onSelectPlayer }: PinnedSidebarProps) {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const s = useStyles();

  const linkClass = (isActive: boolean) => isActive ? s.linkActive : s.link;

  return (
    <aside style={s.sidebar} data-testid="pinned-sidebar">
      <nav style={s.nav}>
        <NavLink to="/songs" style={({ isActive }) => linkClass(isActive)}>
          <span style={s.linkIcon}><IoMusicalNotes size={20} /></span>
          {t('nav.songs')}
        </NavLink>
        {player && (
          <NavLink to="/suggestions" style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoSparkles size={20} /></span>
            {t('nav.suggestions')}
          </NavLink>
        )}
        {player && (
          <NavLink to="/statistics" style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoStatsChart size={20} /></span>
            {t('nav.statistics')}
          </NavLink>
        )}
        {/* v8 ignore start -- player-gated link */}
        {player && (
          <NavLink to="/rivals" style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoPeople size={20} /></span>
            {t('nav.rivals', 'Rivals')}
          </NavLink>
        )}
        {/* v8 ignore stop */}
        {/* v8 ignore start -- shop-visibility link */}
        {!settings.hideItemShop && (
          <NavLink to="/shop" style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoBagHandle size={20} /></span>
            {t('nav.shop', 'Shop')}
          </NavLink>
        )}
        {/* v8 ignore stop */}
        <div style={s.spacer} />
        {player ? (
          <Link to="/statistics" style={s.link}>
            <span style={s.linkIcon}><IoPerson size={20} /></span>
            {player.displayName}
          </Link>
        ) : (
          <button style={s.selectPlayerBtn} onClick={onSelectPlayer}>
            <span style={s.linkIcon}><IoPerson size={20} /></span>
            {t('common.selectPlayerProfile')}
          </button>
        )}
        {player && (
          <button style={s.deselectBtn} onClick={onDeselect}>
            {t('common.deselect')}
          </button>
        )}
        <NavLink to="/settings" style={({ isActive }) => linkClass(isActive)}>
          <span style={s.linkIcon}><IoSettings size={20} /></span>
          {t('nav.settings')}
        </NavLink>
      </nav>
    </aside>
  );
}

/** Exported styles for NavigationDemo cross-consumer. */
export { useStyles as usePinnedSidebarStyles };

function useStyles() {
  return useMemo(() => {
    const bgTransition = transitions(
      transition(CssProp.backgroundColor, LINK_TRANSITION_MS),
      transition(CssProp.borderColor, LINK_TRANSITION_MS),
      transition(CssProp.boxShadow, LINK_TRANSITION_MS),
      transition(CssProp.color, LINK_TRANSITION_MS),
    );
    const link: CSSProperties = {
      ...flexRow,
      gap: Gap.lg,
      height: Layout.entryRowHeight,
      padding: padding(0, Gap.xl),
      color: Colors.textPrimary,
      textDecoration: CssValue.none,
      fontSize: Font.lg,
      fontWeight: Weight.semibold,
      borderRadius: Radius.full,
      boxSizing: BoxSizing.borderBox,
      backgroundColor: CssValue.transparent,
      border: border(Border.thin, CssValue.transparent),
      boxShadow: CssValue.none,
      transition: bgTransition,
    };
    return {
      sidebar: {
        width: Layout.sidebarWidth,
        height: '100%',
        flexShrink: 0,
        ...flexColumn,
        overflow: Overflow.hidden,
        background: CssValue.transparent,
        zIndex: ZIndex.base,
        pointerEvents: PointerEvents.auto,
      } as CSSProperties,
      nav: {
        ...flexColumn,
        flex: 1,
        padding: padding(Gap.md, 0, Gap.md, Gap.md),
        gap: Gap.xs,
      } as CSSProperties,
      spacer: { flex: 1 } as CSSProperties,
      link,
      linkActive: {
        ...link,
        ...purpleGlass,
        color: Colors.textPrimary,
      } as CSSProperties,
      linkIcon: {
        ...flexRow,
        flexShrink: 0,
      } as CSSProperties,
      selectPlayerBtn: {
        ...link,
        background: CssValue.none,
        cursor: Cursor.pointer,
      } as CSSProperties,
      deselectBtn: {
        ...flexCenter,
        margin: margin(Gap.xs, Gap.md),
        padding: padding(Gap.md, Gap.xl),
        borderRadius: Radius.full,
        border: border(Border.thin, Colors.statusRed),
        backgroundColor: Colors.dangerBg,
        color: Colors.textPrimary,
        fontSize: Font.sm,
        fontWeight: Weight.semibold,
        cursor: Cursor.pointer,
      } as CSSProperties,
    };
  }, []);
}
