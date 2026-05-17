/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { IoMusicalNotes, IoPeople, IoSparkles, IoStatsChart, IoSettings, IoTrophy } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import type { SelectedProfile } from '../../../hooks/data/useSelectedProfile';
import { useMediaQuery } from '../../../hooks/ui/useMediaQuery';
import fx from '../../../styles/effects.module.css';
import { paddingWithSafeAreaBottom } from '../../../utils/safeAreaStyles';
import { getStatisticsNavigationPath } from '../../../utils/profileNavigation';
import { TabKey } from '@festival/core';
import {
  Colors, Font, Weight, Gap, ZIndex, Layout,
  Display, Align, Justify, Position, Cursor, CssValue, CssProp, BoxSizing,
  flexColumn, flexCenter, transition, Border, padding, border,
  NAV_TRANSITION_MS,
} from '@festival/theme';
export type { TabKey };

const TOUCH_NAV_MOVEMENT_THRESHOLD = 12;
const POINTER_NAV_CLICK_SUPPRESSION_MS = 700;
const BOTTOM_NAV_TAB_MIN_HEIGHT = Layout.fabSize;
const SPACIOUS_BOTTOM_NAV_QUERY = '(min-width: 600px)';

type BottomNavTab = { key: TabKey; label: string; icon: React.ReactNode; path?: string; activeKeys?: readonly TabKey[] };

export default function BottomNav({ player, selectedProfile = null, activeTab, onTabClick }: {
  player: TrackedPlayer | null;
  selectedProfile?: SelectedProfile | null;
  activeTab: TabKey | null;
  onTabClick: (tab: TabKey, path?: string) => void;
}) {
  const { t } = useTranslation();
  const s = useStyles();
  const canShowSplitCompetitiveTabs = useMediaQuery(SPACIOUS_BOTTOM_NAV_QUERY);
  const [pendingTab, setPendingTab] = useState<TabKey | null>(null);
  const pendingTouchNavRef = useRef<{ pointerId: number; tab: TabKey; clientX: number; clientY: number } | null>(null);
  const lastPointerNavRef = useRef<{ tab: TabKey; timeStamp: number } | null>(null);
  const statisticsPath = getStatisticsNavigationPath(player, selectedProfile);
  const showSuggestions = !!player || selectedProfile?.type === 'band';
  const showSplitCompetitiveTabs = !!player && canShowSplitCompetitiveTabs;
  const tabs: BottomNavTab[] = [
    { key: TabKey.Songs, label: t('nav.songs'), icon: <IoMusicalNotes size={20} /> },
    ...(showSuggestions ? [{ key: TabKey.Suggestions, label: t('nav.suggestions'), icon: <IoSparkles size={20} /> }] : []),
    ...(showSplitCompetitiveTabs
      ? [
        { key: TabKey.Leaderboards, label: t('nav.leaderboards'), icon: <IoTrophy size={20} />, path: '/leaderboards' },
        { key: TabKey.Rivals, label: t('nav.rivals'), icon: <IoPeople size={20} />, path: '/rivals' },
      ]
      : [{
        key: player ? TabKey.Compete : TabKey.Leaderboards,
        label: player ? t('nav.compete') : t('nav.leaderboards'),
        icon: <IoTrophy size={20} />,
        path: player ? '/compete' : '/leaderboards',
        activeKeys: player ? [TabKey.Leaderboards, TabKey.Rivals] : undefined,
      }]),
    ...(statisticsPath ? [{ key: TabKey.Statistics, label: t('nav.statistics'), icon: <IoStatsChart size={20} />, path: statisticsPath }] : []),
    { key: TabKey.Settings, label: t('nav.settings'), icon: <IoSettings size={20} /> },
  ];
  const visualActiveTab = pendingTab ?? activeTab;
  const isTabVisuallyActive = useCallback((tab: BottomNavTab) => (
    visualActiveTab === tab.key || (visualActiveTab != null && tab.activeKeys?.includes(visualActiveTab))
  ), [visualActiveTab]);

  useEffect(() => {
    if (pendingTab && pendingTab === activeTab) setPendingTab(null);
  }, [activeTab, pendingTab]);

  const commitTabNavigation = useCallback((tab: TabKey, path?: string) => {
    setPendingTab(tab);
    onTabClick(tab, path);
  }, [onTabClick]);

  const handlePointerDown = useCallback((tab: TabKey, event: ReactPointerEvent<HTMLButtonElement>) => {
    setPendingTab(tab);
    if ((event.button ?? 0) !== 0 || event.pointerType === 'mouse') return;
    pendingTouchNavRef.current = { pointerId: event.pointerId, tab, clientX: event.clientX, clientY: event.clientY };
  }, []);

  const handlePointerUp = useCallback((tab: TabKey, path: string | undefined, event: ReactPointerEvent<HTMLButtonElement>) => {
    const pending = pendingTouchNavRef.current;
    pendingTouchNavRef.current = null;
    if (!pending || pending.pointerId !== event.pointerId || pending.tab !== tab) return;

    const moved = Math.hypot(event.clientX - pending.clientX, event.clientY - pending.clientY);
    if (moved > TOUCH_NAV_MOVEMENT_THRESHOLD) {
      setPendingTab(null);
      return;
    }

    event.preventDefault();
    lastPointerNavRef.current = { tab, timeStamp: event.timeStamp };
    commitTabNavigation(tab, path);
  }, [commitTabNavigation]);

  const handleClick = useCallback((tab: TabKey, path: string | undefined, event: ReactMouseEvent<HTMLButtonElement>) => {
    const pointerNav = lastPointerNavRef.current;
    if (pointerNav && pointerNav.tab === tab && event.timeStamp - pointerNav.timeStamp < POINTER_NAV_CLICK_SUPPRESSION_MS) {
      event.preventDefault();
      return;
    }

    commitTabNavigation(tab, path);
  }, [commitTabNavigation]);

  return (
    <nav className={fx.navFrosted} style={s.nav}>
      {tabs.map((tab) => (
        <button
          key={tab.key}
          onPointerDown={(event) => handlePointerDown(tab.key, event)}
          onPointerUp={(event) => handlePointerUp(tab.key, tab.path, event)}
          onPointerCancel={() => setPendingTab(null)}
          onClick={(event) => handleClick(tab.key, tab.path, event)}
          data-testid={`bottom-nav-${tab.key}`}
          data-tab-key={tab.key}
          data-pending={pendingTab === tab.key ? 'true' : undefined}
          style={isTabVisuallyActive(tab) ? s.tabActive : s.tab}
        >
          <span style={s.tabIcon}>{tab.icon}</span>
          {tab.label}
        </button>
      ))}
    </nav>
  );
}

/** Exported for NavigationDemo cross-consumer. */
export { fx as bottomNavCss };

function useStyles() {
  return useMemo(() => {
    const tab: CSSProperties = {
      flex: 1,
      ...flexColumn,
      alignItems: Align.center,
      gap: Gap.xs,
      padding: padding(Gap.sm, Gap.xl),
      color: Colors.textTertiary,
      fontSize: Font.xs,
      fontWeight: Weight.normal,
      background: CssValue.none,
      border: CssValue.none,
      cursor: Cursor.pointer,
      minWidth: Layout.bottomNavTabButtonMin,
      minHeight: BOTTOM_NAV_TAB_MIN_HEIGHT,
      boxSizing: BoxSizing.borderBox,
      transition: transition(CssProp.color, NAV_TRANSITION_MS),
    };
    return {
      nav: {
        display: Display.flex,
        justifyContent: Justify.around,
        alignItems: Align.center,
        borderTop: border(Border.thin, Colors.glassBorder),
        flexShrink: 0,
        zIndex: ZIndex.popover,
        position: Position.relative,
        padding: paddingWithSafeAreaBottom(Gap.sm, Gap.none, Gap.md),
        touchAction: CssValue.none,
      } as CSSProperties,
      tab,
      tabActive: {
        ...tab,
        color: Colors.accentPurple,
        fontWeight: Weight.bold,
      } as CSSProperties,
      tabIcon: {
        ...flexCenter,
        fontSize: Font.xl,
        lineHeight: Gap.section,
      } as CSSProperties,
    };
  }, []);
}
