import { NavLink, Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoPerson, IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings, IoBagHandle, IoPeople } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import { useSettings } from '../../../contexts/SettingsContext';
import s from './PinnedSidebar.module.css';

interface PinnedSidebarProps {
  player: TrackedPlayer | null;
  onDeselect: () => void;
  onSelectPlayer: () => void;
}

export default function PinnedSidebar({ player, onDeselect, onSelectPlayer }: PinnedSidebarProps) {
  const { t } = useTranslation();
  const { settings } = useSettings();

  return (
    <aside className={s.sidebar} data-testid="pinned-sidebar">
      <nav className={s.nav}>
        <NavLink to="/songs" className={({ isActive }) => isActive ? s.linkActive : s.link}>
          <span className={s.linkIcon}><IoMusicalNotes size={20} /></span>
          {t('nav.songs')}
        </NavLink>
        {player && (
          <NavLink to="/suggestions" className={({ isActive }) => isActive ? s.linkActive : s.link}>
            <span className={s.linkIcon}><IoSparkles size={20} /></span>
            {t('nav.suggestions')}
          </NavLink>
        )}
        {player && (
          <NavLink to="/statistics" className={({ isActive }) => isActive ? s.linkActive : s.link}>
            <span className={s.linkIcon}><IoStatsChart size={20} /></span>
            {t('nav.statistics')}
          </NavLink>
        )}
        {/* v8 ignore start -- player-gated link */}
        {player && (
          <NavLink to="/rivals" className={({ isActive }) => isActive ? s.linkActive : s.link}>
            <span className={s.linkIcon}><IoPeople size={20} /></span>
            {t('nav.rivals', 'Rivals')}
          </NavLink>
        )}
        {/* v8 ignore stop */}
        {/* v8 ignore start -- shop-visibility link */}
        {!settings.hideItemShop && (
          <NavLink to="/shop" className={({ isActive }) => isActive ? s.linkActive : s.link}>
            <span className={s.linkIcon}><IoBagHandle size={20} /></span>
            {t('nav.shop', 'Shop')}
          </NavLink>
        )}
        {/* v8 ignore stop */}
        <div className={s.spacer} />
        {player ? (
          <Link to="/statistics" className={s.link}>
            <span className={s.linkIcon}><IoPerson size={20} /></span>
            {player.displayName}
          </Link>
        ) : (
          <button className={s.selectPlayerBtn} onClick={onSelectPlayer}>
            <span className={s.linkIcon}><IoPerson size={20} /></span>
            {t('common.selectPlayerProfile')}
          </button>
        )}
        {player && (
          <button className={s.deselectBtn} onClick={onDeselect}>
            {t('common.deselect')}
          </button>
        )}
        <NavLink to="/settings" className={({ isActive }) => isActive ? s.linkActive : s.link}>
          <span className={s.linkIcon}><IoSettings size={20} /></span>
          {t('nav.settings')}
        </NavLink>
      </nav>
    </aside>
  );
}
