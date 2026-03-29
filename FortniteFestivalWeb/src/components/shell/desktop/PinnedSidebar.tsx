/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { NavLink, Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoPerson, IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings, IoBagHandle, IoPeople, IoTrophy } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import { useSettings } from '../../../contexts/SettingsContext';
import { useFeatureFlags } from '../../../contexts/FeatureFlagsContext';
import { useScrollContainer } from '../../../contexts/ScrollContainerContext';
import {
  Colors, Font, Weight, Gap, Radius, Border, Layout, ZIndex,
  Display, Align, Justify, Position, Cursor, BoxSizing, CssValue, CssProp,
  flexColumn, flexRow, flexCenter, purpleGlass, btnDanger, transition, transitions, padding, border, margin,
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
  const flags = useFeatureFlags();
  const scrollRef = useScrollContainer();
  const s = useStyles();

  const linkClass = (isActive: boolean) => isActive ? s.linkActive : s.link;

  return (
    <aside style={s.sidebar} data-testid="pinned-sidebar" onWheel={(e) => { scrollRef.current?.scrollBy({ top: e.deltaY, left: e.deltaX }); }}>
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
        {player && flags.rivals && (
          <NavLink to="/rivals" style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoPeople size={20} /></span>
            {t('nav.rivals', 'Rivals')}
          </NavLink>
        )}
        {/* v8 ignore stop */}
        {flags.leaderboards && (
        <NavLink to="/leaderboards" style={({ isActive }) => linkClass(isActive)}>
          <span style={s.linkIcon}><IoTrophy size={20} /></span>
          {t('nav.leaderboards')}
        </NavLink>
        )}
        {/* v8 ignore start -- shop-visibility link */}
        {flags.shop && !settings.hideItemShop && (
          <NavLink to="/shop" style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoBagHandle size={20} /></span>
            {t('nav.shop', 'Shop')}
          </NavLink>
        )}
        {/* v8 ignore stop */}
        <div style={s.spacer} />
        {player ? (
          <div style={s.playerRow}>
            <Link to="/statistics" style={s.playerLink}>
              <span style={s.linkIcon}><IoPerson size={20} /></span>
              {player.displayName}
            </Link>
            <button style={s.deselectBtn} onClick={onDeselect}>
              {t('common.deselect')}
            </button>
          </div>
        ) : (
          <button style={s.selectPlayerBtn} onClick={onSelectPlayer}>
            <span style={s.linkIcon}><IoPerson size={20} /></span>
            {t('common.selectPlayerProfile')}
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
      '--frosted-card': '1',
    } as CSSProperties;
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
        width: 20,
        justifyContent: Justify.center,
      } as CSSProperties,
      playerRow: {
        display: Display.flex,
        alignItems: Align.center,
      } as CSSProperties,
      playerLink: {
        ...link,
        flex: 1,
      } as CSSProperties,
      selectPlayerBtn: {
        ...link,
        background: CssValue.none,
        cursor: Cursor.pointer,
      } as CSSProperties,
      deselectBtn: {
        ...btnDanger,
        padding: padding(Gap.sm, Gap.xl),
        fontSize: Font.sm,
        whiteSpace: 'nowrap',
        marginLeft: 'auto',
      } as CSSProperties,
    };
  }, []);
}
