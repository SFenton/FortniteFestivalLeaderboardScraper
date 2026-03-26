/**
 * First-run demo: interactive navigation preview.
 * Reuses production style objects from BottomNav, PinnedSidebar, and Sidebar
 * so the demo visuals stay in sync with the real UI.
 */
import { useState, useEffect, useRef, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings } from 'react-icons/io5';
import { TabKey } from '@festival/core';
import {
  Colors, Font, Weight, Gap, Layout, ZIndex, Radius, Border,
  Display, Align, Justify, Position, BoxSizing, Cursor, CssValue, CssProp,
  flexRow, flexColumn, flexCenter, purpleGlass, transition, padding, border,
  Size, TRANSITION_MS, NAV_TRANSITION_MS,
} from '@festival/theme';
import { bottomNavCss } from '../../../../components/shell/mobile/BottomNav';
import { usePinnedSidebarStyles } from '../../../../components/shell/desktop/PinnedSidebar';
import { useIsMobileChrome, useIsWideDesktop } from '../../../../hooks/ui/useIsMobile';
import { usePlayerData } from '../../../../contexts/PlayerDataContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import FadeIn from '../../../../components/page/FadeIn';
import { sidebarStyles as sidebarCss } from '../../../../components/shell/desktop/sidebarStyles';

const centerWrap: CSSProperties = { ...flexCenter, width: CssValue.full, height: CssValue.full };
const compactNav: CSSProperties = { width: CssValue.full, maxWidth: 260, padding: 0, flex: CssValue.none };

type Tab = { key: TabKey; label: string; icon: React.ReactNode };

function useTabs(): Tab[] {
  const { t } = useTranslation();
  const { playerData } = usePlayerData();
  const all: Tab[] = [
    { key: TabKey.Songs, label: t('nav.songs'), icon: <IoMusicalNotes size={Size.iconDefault} /> },
    { key: TabKey.Suggestions, label: t('nav.suggestions'), icon: <IoSparkles size={Size.iconDefault} /> },
    { key: TabKey.Statistics, label: t('nav.statistics'), icon: <IoStatsChart size={Size.iconDefault} /> },
    { key: TabKey.Settings, label: t('nav.settings'), icon: <IoSettings size={Size.iconDefault} /> },
  ];
  if (!playerData) return all.filter(tab => tab.key === TabKey.Songs || tab.key === TabKey.Settings);
  return all;
}

/* ── Mobile: BottomNav replica ── */

function MobileNav({ tabs }: { tabs: Tab[] }) {
  /* v8 ignore start -- tabs always has ≥2 entries from useTabs() */
  const [active, setActive] = useState<TabKey>(tabs[0]?.key ?? TabKey.Songs);
  /* v8 ignore stop */
  const navRef = useRef<HTMLElement>(null);
  const [visibleTabs, setVisibleTabs] = useState(tabs);

  useEffect(() => {
    const el = navRef.current;
    if (!el) return;
    /* v8 ignore start -- ResizeObserver callback depends on real DOM measurements */
    const ro = new ResizeObserver(entries => {
      const width = entries[0]?.contentRect.width ?? 0;
      if (width < tabs.length * Layout.bottomNavTabMinWidth) {
        setVisibleTabs([tabs[0]!, tabs[tabs.length - 1]!]);
      } else {
        setVisibleTabs(tabs);
      }
    });
    /* v8 ignore stop */
    ro.observe(el);
    return () => ro.disconnect();
  }, [tabs]);

  const st = useMobileNavStyles();

  return (
    <FadeIn delay={TRANSITION_MS} style={{ width: '100%' }}>
      <nav ref={navRef} className={bottomNavCss.navFrosted} style={st.nav}>
      {visibleTabs.map((tab) => {
        const isActive = active === tab.key;
        return (
          <button
            key={tab.key}
            onClick={() => setActive(tab.key)}
            style={isActive ? st.tabActive : st.tab}
          >
            <span style={st.tabIcon}>{tab.icon}</span>
            {tab.label}
          </button>
        );
      })}
      </nav>
    </FadeIn>
  );
}

function useMobileNavStyles() {
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
        borderRadius: Radius.md,
        width: CssValue.full,
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

/* ── Shared: visible-tab calculator ── */

function useVisibleTabs(tabs: Tab[], itemHeight: number) {
  const [visibleTabs, setVisibleTabs] = useState(tabs);
  const h = useSlideHeight();

  useEffect(() => {
    if (!h) return;
    const maxItems = Math.max(2, Math.floor(h / itemHeight));
    if (maxItems >= tabs.length) {
      setVisibleTabs(tabs);
    } else {
      const middle = tabs.slice(1, -1);
      const midSlots = maxItems - 2;
      setVisibleTabs([tabs[0]!, ...middle.slice(0, midSlots), tabs[tabs.length - 1]!]);
    }
  }, [h, tabs, itemHeight]);

  return { visibleTabs };
}

/* ── Desktop: PinnedSidebar replica ── */

function DesktopNav({ tabs }: { tabs: Tab[] }) {
  const { visibleTabs } = useVisibleTabs(tabs, Layout.pinnedSidebarItemHeight);
  /* v8 ignore start -- tabs always has ≥2 entries from useTabs() */
  const [active, setActive] = useState<TabKey>(tabs[0]?.key ?? TabKey.Songs);
  /* v8 ignore stop */
  const ps = usePinnedSidebarStyles();

  return (
    <FadeIn delay={TRANSITION_MS} style={centerWrap}>
      <aside style={ps.nav}>
        {visibleTabs.map((tab) => {
          const isActive = active === tab.key;
          return (
            <button
              key={tab.key}
              onClick={() => setActive(tab.key)}
              style={isActive ? ps.linkActive : ps.link}
            >
              <span style={ps.linkIcon}>{tab.icon}</span>
              {tab.label}
            </button>
          );
        })}
      </aside>
    </FadeIn>
  );
}

/* ── Desktop compact: Sidebar flyout replica ── */

function CompactNav({ tabs }: { tabs: Tab[] }) {
  const { visibleTabs } = useVisibleTabs(tabs, Layout.sidebarItemHeight);
  /* v8 ignore start -- tabs always has ≥2 entries from useTabs() */
  const [active, setActive] = useState<TabKey>(tabs[0]?.key ?? TabKey.Songs);
  /* v8 ignore stop */

  return (
    <FadeIn delay={TRANSITION_MS} style={centerWrap}>
      <div style={{ ...sidebarCss.sidebarNav, ...compactNav }}>
        {visibleTabs.map((tab) => {
          const isActive = active === tab.key;
          return (
            <button
              key={tab.key}
              onClick={() => setActive(tab.key)}
              style={isActive ? sidebarCss.sidebarLinkActive : sidebarCss.sidebarLink}
            >
              {tab.label}
            </button>
          );
        })}
      </div>
    </FadeIn>
  );
}

export default function NavigationDemo() {
  const isMobile = useIsMobileChrome();
  const isWide = useIsWideDesktop();
  const tabs = useTabs();
  if (isMobile) return <MobileNav tabs={tabs} />;
  if (isWide) return <DesktopNav tabs={tabs} />;
  return <CompactNav tabs={tabs} />;
}
