import { useEffect, useLayoutEffect, useState, useCallback, useRef } from 'react';
import { NavLink, Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoPerson } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import s from './Sidebar.module.css';

const SIDEBAR_DURATION = 250;

interface SidebarProps {
  player: TrackedPlayer | null;
  open: boolean;
  onClose: () => void;
  onDeselect: () => void;
  onSelectPlayer: () => void;
}

export default function Sidebar({ player, open, onClose, onDeselect, onSelectPlayer }: SidebarProps) {
  const { t } = useTranslation();
  const sidebarRef = useRef<HTMLDivElement>(null);
  const [mounted, setMounted] = useState(false);
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    if (open) setMounted(true);
    else setVisible(false);
  }, [open]);

  /* v8 ignore start -- animation: rAF + getBoundingClientRect */
  useLayoutEffect(() => {
    if (mounted && open) {
      sidebarRef.current?.getBoundingClientRect();
      const id = requestAnimationFrame(() => setVisible(true));
      return () => cancelAnimationFrame(id);
    }
  }, [mounted, open]);

  const handleTransitionEnd = useCallback(() => {
    if (!visible) setMounted(false);
  }, [visible]);
  /* v8 ignore stop */

  useEffect(() => {
    if (!mounted) return;
    function handleClick(e: MouseEvent) {
      if (sidebarRef.current && !sidebarRef.current.contains(e.target as Node)) onClose();
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [mounted, onClose]);

  if (!mounted) return null;

  return (
    <>
      <div
        className={s.overlay}
        style={{ opacity: visible ? 1 : 0, transition: `opacity ${SIDEBAR_DURATION}ms ease` }}
        onClick={onClose}
      />
      <div
        ref={sidebarRef}
        className={s.sidebar}
        style={{ transform: visible ? 'translateX(0)' : 'translateX(-100%)', transition: `transform ${SIDEBAR_DURATION}ms ease` }}
        onTransitionEnd={handleTransitionEnd}
      >
        <div className={s.sidebarHeader}>
          <span className={s.brand}>{t('common.brandName')}</span>
        </div>
        <nav className={s.sidebarNav}>
          <NavLink to="/songs" onClick={onClose} className={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
            {t('nav.songs')}
          </NavLink>
          {player && (
            <NavLink to="/suggestions" onClick={onClose} className={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              {t('nav.suggestions')}
            </NavLink>
          )}
          {player && (
            <NavLink to="/statistics" onClick={onClose} className={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              {t('nav.statistics')}
            </NavLink>
          )}
        </nav>
        <div className={s.sidebarFooter}>
          {player ? (
            <div className={s.sidebarPlayerRow}>
              <Link to="/statistics" onClick={onClose} className={s.playerLink}>
                <span className={s.profileCircle}><IoPerson size={14} /></span>
                {player.displayName}
              </Link>
              <button className={s.deselectBtn} onClick={onDeselect}>
                {t('common.deselect')}
              </button>
            </div>
          ) : (
            <button className={s.selectPlayerBtn} onClick={onSelectPlayer}>
              <span className={s.profileCircleEmpty}><IoPerson size={14} /></span>
              {t('common.selectPlayerProfile')}
            </button>
          )}
          <NavLink to="/settings" onClick={onClose} className={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
            {t('nav.settings')}
          </NavLink>
        </div>
      </div>
    </>
  );
}
