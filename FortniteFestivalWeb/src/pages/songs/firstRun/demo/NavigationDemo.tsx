/**
 * First-run demo: interactive navigation preview.
 * Reuses production CSS modules from BottomNav, PinnedSidebar, and Sidebar
 * so the demo visuals stay in sync with the real UI.
 */
import { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings } from 'react-icons/io5';
import { TabKey } from '@festival/core';
import { Size, Layout, TRANSITION_MS } from '@festival/theme';
import { useIsMobileChrome, useIsWideDesktop } from '../../../../hooks/ui/useIsMobile';
import { usePlayerData } from '../../../../contexts/PlayerDataContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import FadeIn from '../../../../components/page/FadeIn';
import bottomCss from '../../../../components/shell/mobile/BottomNav.module.css';
import pinnedCss from '../../../../components/shell/desktop/PinnedSidebar.module.css';
import sidebarCss from '../../../../components/shell/desktop/Sidebar.module.css';
import s from '../pages/Navigation.module.css';

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
  const [active, setActive] = useState<TabKey>(tabs[0]?.key ?? TabKey.Songs);
  const navRef = useRef<HTMLElement>(null);
  const [visibleTabs, setVisibleTabs] = useState(tabs);

  useEffect(() => {
    const el = navRef.current;
    if (!el) return;
    const ro = new ResizeObserver(entries => {
      const width = entries[0]?.contentRect.width ?? 0;
      if (width < tabs.length * Layout.bottomNavTabMinWidth) {
        setVisibleTabs([tabs[0]!, tabs[tabs.length - 1]!]);
      } else {
        setVisibleTabs(tabs);
      }
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [tabs]);

  return (
    <FadeIn delay={TRANSITION_MS} style={{ width: '100%' }}>
      <nav ref={navRef} className={`${bottomCss.nav} ${s.mobileNav}`}>
      {visibleTabs.map((tab) => {
        const isActive = active === tab.key;
        return (
          <button
            key={tab.key}
            onClick={() => setActive(tab.key)}
            className={isActive ? bottomCss.tabActive : bottomCss.tab}
          >
            <span className={bottomCss.tabIcon}>{tab.icon}</span>
            {tab.label}
          </button>
        );
      })}
      </nav>
    </FadeIn>
  );
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
  const [active, setActive] = useState<TabKey>(tabs[0]?.key ?? TabKey.Songs);

  return (
    <FadeIn delay={TRANSITION_MS} className={s.centerWrap}>
      <aside className={`${pinnedCss.nav} ${s.pinnedNav}`}>
        {visibleTabs.map((tab) => {
          const isActive = active === tab.key;
          return (
            <button
              key={tab.key}
              onClick={() => setActive(tab.key)}
              className={isActive ? pinnedCss.linkActive : pinnedCss.link}
            >
              <span className={pinnedCss.linkIcon}>{tab.icon}</span>
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
  const [active, setActive] = useState<TabKey>(tabs[0]?.key ?? TabKey.Songs);

  return (
    <FadeIn delay={TRANSITION_MS} className={s.centerWrap}>
      <div className={`${sidebarCss.sidebarNav} ${s.compactNav}`}>
        {visibleTabs.map((tab) => {
          const isActive = active === tab.key;
          return (
            <button
              key={tab.key}
              onClick={() => setActive(tab.key)}
              className={isActive ? sidebarCss.sidebarLinkActive : sidebarCss.sidebarLink}
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
