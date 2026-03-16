import { useEffect, useLayoutEffect, useState, useCallback, useRef } from 'react';
import { NavLink, Link, useLocation } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoPerson } from 'react-icons/io5';
import type { TrackedPlayer } from '../../hooks/useTrackedPlayer';
import { Colors, Font, Gap, Radius, frostedCard } from '@festival/theme';

const SIDEBAR_DURATION = 250;
const SIDEBAR_WIDTH = 280;

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
        style={{ ...styles.overlay, opacity: visible ? 1 : 0, transition: `opacity ${SIDEBAR_DURATION}ms ease` }}
        onClick={onClose}
      />
      <div
        ref={sidebarRef}
        style={{ ...styles.sidebar, transform: visible ? 'translateX(0)' : 'translateX(-100%)', transition: `transform ${SIDEBAR_DURATION}ms ease` }}
        onTransitionEnd={handleTransitionEnd}
      >
        <div style={styles.sidebarHeader}>
          <span style={styles.brand}>{t('common.brandName')}</span>
        </div>
        <nav style={styles.sidebarNav}>
          <NavLink to="/songs" onClick={onClose} style={({ isActive }) => ({ ...styles.sidebarLink, ...(isActive ? styles.sidebarLinkActive : {}) })}>
            {t('nav.songs')}
          </NavLink>
          {player && (
            <NavLink to="/suggestions" onClick={onClose} style={({ isActive }) => ({ ...styles.sidebarLink, ...(isActive ? styles.sidebarLinkActive : {}) })}>
              {t('nav.suggestions')}
            </NavLink>
          )}
          {player && (
            <NavLink to="/statistics" onClick={onClose} style={({ isActive }) => ({ ...styles.sidebarLink, ...(isActive ? styles.sidebarLinkActive : {}) })}>
              {t('nav.statistics')}
            </NavLink>
          )}
        </nav>
        <div style={styles.sidebarFooter}>
          {player ? (
            <div style={styles.sidebarPlayerRow}>
              <Link to="/statistics" onClick={onClose} style={{ ...styles.sidebarLink, flex: 1, display: 'flex', alignItems: 'center' }}>
                <span style={styles.profileCircle}><IoPerson size={14} /></span>
                {player.displayName}
              </Link>
              <button style={{ ...styles.deselectBtn, marginRight: Gap.section }} onClick={onDeselect}>
                Deselect
              </button>
            </div>
          ) : (
            <button style={{ ...styles.sidebarLink, display: 'flex', alignItems: 'center', background: 'none', border: 'none', cursor: 'pointer' }} onClick={onSelectPlayer}>
              <span style={styles.profileCircleEmpty}><IoPerson size={14} /></span>
              Select Player
            </button>
          )}
          <NavLink to="/settings" onClick={onClose} style={({ isActive }) => ({ ...styles.sidebarLink, ...(isActive ? styles.sidebarLinkActive : {}) })}>
            {t('nav.settings')}
          </NavLink>
        </div>
      </div>
    </>
  );
}

const styles: Record<string, React.CSSProperties> = {
  overlay: {
    position: 'fixed',
    inset: 0,
    backgroundColor: Colors.overlayDark,
    zIndex: 200,
  },
  sidebar: {
    position: 'fixed',
    top: 0,
    left: 0,
    bottom: 0,
    width: SIDEBAR_WIDTH,
    ...frostedCard,
    backgroundColor: Colors.backgroundCard,
    borderRight: `1px solid ${Colors.glassBorder}`,
    zIndex: 201,
    display: 'flex',
    flexDirection: 'column' as const,
    overflow: 'hidden',
  },
  sidebarHeader: {
    padding: `${Gap.section}px ${Gap.section}px ${Gap.xl}px`,
    borderBottom: `1px solid ${Colors.borderSubtle}`,
  },
  brand: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.accentPurple,
  },
  sidebarNav: {
    display: 'flex',
    flexDirection: 'column' as const,
    padding: `${Gap.md}px 0`,
    flex: 1,
  },
  sidebarFooter: {
    borderTop: `1px solid ${Colors.borderSubtle}`,
    padding: `${Gap.md}px 0`,
  },
  sidebarLink: {
    display: 'block',
    padding: `${Gap.xl}px ${Gap.section}px`,
    color: Colors.textSecondary,
    textDecoration: 'none',
    fontSize: Font.md,
    transition: 'background-color 0.15s, color 0.15s',
  },
  sidebarLinkActive: {
    color: Colors.textPrimary,
    backgroundColor: Colors.surfaceSubtle,
    borderLeft: `3px solid ${Colors.accentPurple}`,
  },
  sidebarPlayerRow: {
    display: 'flex',
    alignItems: 'center',
  },
  profileCircle: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 28,
    height: 28,
    borderRadius: '50%',
    backgroundColor: Colors.surfaceSubtle,
    border: `1px solid ${Colors.borderSubtle}`,
    flexShrink: 0,
    marginRight: Gap.md,
  },
  profileCircleEmpty: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 28,
    height: 28,
    borderRadius: '50%',
    backgroundColor: '#D0D5DD',
    border: 'none',
    color: '#4A5568',
    flexShrink: 0,
    marginRight: Gap.md,
  },
  deselectBtn: {
    padding: `${Gap.sm}px ${Gap.xl}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.dangerBg,
    color: Colors.textSecondary,
    fontSize: Font.sm,
    cursor: 'pointer',
    whiteSpace: 'nowrap' as const,
  },
};
