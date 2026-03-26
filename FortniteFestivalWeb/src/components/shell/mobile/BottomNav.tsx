/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import { IS_PWA } from '@festival/ui-utils';
import fx from '../../../styles/effects.module.css';
import { TabKey } from '@festival/core';
import {
  Colors, Font, Weight, Gap, ZIndex, Layout,
  Display, Align, Justify, Position, Cursor, CssValue, CssProp,
  flexColumn, flexCenter, transition, Border, padding, border,
  NAV_TRANSITION_MS,
} from '@festival/theme';
export type { TabKey };

export default function BottomNav({ player, activeTab, onTabClick }: {
  player: TrackedPlayer | null;
  activeTab: TabKey;
  onTabClick: (tab: TabKey) => void;
}) {
  const { t } = useTranslation();
  const s = useStyles();
  const tabs: { key: TabKey; label: string; icon: React.ReactNode }[] = [
    { key: TabKey.Songs, label: t('nav.songs'), icon: <IoMusicalNotes size={20} /> },
    ...(player ? [{ key: TabKey.Suggestions, label: t('nav.suggestions'), icon: <IoSparkles size={20} /> }] : []),
    ...(player ? [{ key: TabKey.Statistics, label: t('nav.statistics'), icon: <IoStatsChart size={20} /> }] : []),
    { key: TabKey.Settings, label: t('nav.settings'), icon: <IoSettings size={20} /> },
  ];

  return (
    /* v8 ignore start -- IS_PWA: PWA detection not available in jsdom */
    <nav className={fx.navFrosted} style={{ ...s.nav, ...(IS_PWA ? { paddingBottom: Gap.section } : undefined) }}>
    {/* v8 ignore stop */}
      {tabs.map((tab) => (
        <button
          key={tab.key}
          onClick={() => onTabClick(tab.key)}
          style={activeTab === tab.key ? s.tabActive : s.tab}
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
        padding: padding(Gap.sm, Gap.none, Gap.md),
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
