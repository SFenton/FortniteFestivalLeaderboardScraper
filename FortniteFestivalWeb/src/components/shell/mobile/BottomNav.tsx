import { useTranslation } from 'react-i18next';
import { IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import { IS_PWA } from '@festival/ui-utils';
import css from './BottomNav.module.css';
import { TabKey } from '@festival/core';
export type { TabKey };

export default function BottomNav({ player, activeTab, onTabClick }: {
  player: TrackedPlayer | null;
  activeTab: TabKey;
  onTabClick: (tab: TabKey) => void;
}) {
  const { t } = useTranslation();
  const tabs: { key: TabKey; label: string; icon: React.ReactNode }[] = [
    { key: TabKey.Songs, label: t('nav.songs'), icon: <IoMusicalNotes size={20} /> },
    ...(player ? [{ key: TabKey.Suggestions, label: t('nav.suggestions'), icon: <IoSparkles size={20} /> }] : []),
    ...(player ? [{ key: TabKey.Statistics, label: t('nav.statistics'), icon: <IoStatsChart size={20} /> }] : []),
    { key: TabKey.Settings, label: t('nav.settings'), icon: <IoSettings size={20} /> },
  ];

  return (
    <nav className={css.nav} style={IS_PWA ? { paddingBottom: 24 } : undefined}>
      {tabs.map((tab) => (
        <button
          key={tab.key}
          onClick={() => onTabClick(tab.key)}
          className={activeTab === tab.key ? css.tabActive : css.tab}
        >
          <span className={css.tabIcon}>{tab.icon}</span>
          {tab.label}
        </button>
      ))}
    </nav>
  );
}
