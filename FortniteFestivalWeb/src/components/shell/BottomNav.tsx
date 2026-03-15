import { useTranslation } from 'react-i18next';
import { IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings } from 'react-icons/io5';
import type { TrackedPlayer } from '../../hooks/useTrackedPlayer';
import { Colors, Font, Gap } from '@festival/theme';
import { IS_PWA } from '../../utils/platform';

type TabKey = 'songs' | 'suggestions' | 'statistics' | 'settings';

export default function BottomNav({ player, activeTab, onTabClick }: {
  player: TrackedPlayer | null;
  activeTab: TabKey;
  onTabClick: (tab: TabKey) => void;
}) {
  const { t } = useTranslation();
  const tabs: { key: TabKey; label: string; icon: React.ReactNode }[] = [
    { key: 'songs', label: t('nav.songs'), icon: <IoMusicalNotes size={20} /> },
    ...(player ? [{ key: 'suggestions' as TabKey, label: t('nav.suggestions'), icon: <IoSparkles size={20} /> }] : []),
    ...(player ? [{ key: 'statistics' as TabKey, label: t('nav.statistics'), icon: <IoStatsChart size={20} /> }] : []),
    { key: 'settings', label: t('nav.settings'), icon: <IoSettings size={20} /> },
  ];

  return (
    <nav style={{ ...styles.bottomNav, ...(IS_PWA ? { paddingBottom: Gap.section } : {}) }}>
      {tabs.map((tab) => (
        <button
          key={tab.key}
          onClick={() => onTabClick(tab.key)}
          style={{
            ...styles.bottomTab,
            ...(activeTab === tab.key ? styles.bottomTabActive : {}),
          }}
        >
          <span style={styles.bottomTabIcon}>{tab.icon}</span>
          {tab.label}
        </button>
      ))}
    </nav>
  );
}

const styles: Record<string, React.CSSProperties> = {
  bottomNav: {
    display: 'flex',
    justifyContent: 'space-around',
    alignItems: 'stretch',
    backgroundColor: Colors.glassNav,
    backdropFilter: 'blur(18px)',
    WebkitBackdropFilter: 'blur(18px)',
    borderTop: `1px solid ${Colors.glassBorder}`,
    flexShrink: 0,
    zIndex: 100,
  },
  bottomTab: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 2,
    padding: `${Gap.md}px 0 ${Gap.sm}px`,
    color: Colors.textMuted,
    fontSize: 10,
    fontWeight: 500,
    background: 'none',
    border: 'none',
    cursor: 'pointer',
  },
  bottomTabActive: {
    color: Colors.purpleTabActive,
    fontWeight: 700,
  },
  bottomTabIcon: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: 24,
  },
};
