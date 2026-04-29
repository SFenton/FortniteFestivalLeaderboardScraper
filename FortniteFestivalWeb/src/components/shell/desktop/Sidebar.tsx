/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useLayoutEffect, useState, useCallback, useRef } from 'react';
import { NavLink, Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoPerson, IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings, IoBagHandle, IoPeople, IoTrophy } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import type { SelectedBandProfile, SelectedProfile } from '../../../hooks/data/useSelectedProfile';
import { useSettings } from '../../../contexts/SettingsContext';
import MarqueeText from '../../common/MarqueeText';
import { sidebarStyles as s } from './sidebarStyles';
import { Routes } from '../../../routes';

const SIDEBAR_DURATION = 250;

interface SidebarProps {
  player: TrackedPlayer | null;
  selectedProfile?: SelectedProfile | null;
  open: boolean;
  onClose: () => void;
  onDeselect: () => void;
  onSelectPlayer: () => void;
}

export default function Sidebar({ player, selectedProfile, open, onClose, onDeselect, onSelectPlayer }: SidebarProps) {
  const { t } = useTranslation();
  const { settings } = useSettings();
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
  const selectedBand = selectedProfile?.type === 'band' ? selectedProfile : null;

  return (
    <>
      <div
        style={{ ...s.overlay, opacity: visible ? 1 : 0, transition: `opacity ${SIDEBAR_DURATION}ms ease` }}
        onClick={onClose}
      />
      <div
        ref={sidebarRef}
        style={{ ...s.sidebar, transform: visible ? 'translateX(0)' : 'translateX(-100%)', transition: `transform ${SIDEBAR_DURATION}ms ease` }}
        onTransitionEnd={handleTransitionEnd}
      >
        <div style={s.sidebarHeader}>
          <span style={s.brand}>{t('common.brandName')}</span>
        </div>
        <nav style={s.sidebarNav}>
          <NavLink to="/songs" onClick={onClose} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
            <span style={s.sidebarLinkIcon}><IoMusicalNotes size={20} /></span>
            {t('nav.songs')}
          </NavLink>
          {player && (
            <NavLink to="/suggestions" onClick={onClose} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              <span style={s.sidebarLinkIcon}><IoSparkles size={20} /></span>
              {t('nav.suggestions')}
            </NavLink>
          )}
          {player && (
            <NavLink to="/statistics" onClick={onClose} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              <span style={s.sidebarLinkIcon}><IoStatsChart size={20} /></span>
              {t('nav.statistics')}
            </NavLink>
          )}
          {/* v8 ignore start -- player-gated link */}
          {player && (
            <NavLink to="/rivals" onClick={onClose} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              <span style={s.sidebarLinkIcon}><IoPeople size={20} /></span>
              {t('nav.rivals', 'Rivals')}
            </NavLink>
          )}
          {/* v8 ignore stop */}
          <NavLink to="/leaderboards" onClick={onClose} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
            <span style={s.sidebarLinkIcon}><IoTrophy size={20} /></span>
            {t('nav.leaderboards')}
          </NavLink>
          {/* v8 ignore start -- shop-visibility link */}
          {!settings.hideItemShop && (
            <NavLink to="/shop" onClick={onClose} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              <span style={s.sidebarLinkIcon}><IoBagHandle size={20} /></span>
              {t('nav.shop', 'Shop')}
            </NavLink>
          )}
          {/* v8 ignore stop */}
        </nav>
        <div style={s.sidebarFooter}>
          {selectedBand ? (
            <SelectedBandPanel band={selectedBand} onClose={onClose} onDeselect={onDeselect} />
          ) : player ? (
            <div style={s.sidebarPlayerRow}>
              <Link to="/statistics" onClick={onClose} style={s.playerLink}>
                <span style={s.sidebarLinkIcon}><IoPerson size={20} /></span>
                <MarqueeText as="p" text={player.displayName} style={s.playerName} />
              </Link>
              <button style={s.deselectBtn} onClick={onDeselect}>
                {t('common.deselect')}
              </button>
            </div>
          ) : (
            <button style={s.selectPlayerBtn} onClick={onSelectPlayer}>
              <span style={s.sidebarLinkIcon}><IoPerson size={20} /></span>
              {t('common.selectPlayerProfile')}
            </button>
          )}
          <NavLink to="/settings" onClick={onClose} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
            <span style={s.sidebarLinkIcon}><IoSettings size={20} /></span>
            {t('nav.settings')}
          </NavLink>
        </div>
      </div>
    </>
  );
}

function SelectedBandPanel({ band, onClose, onDeselect }: { band: SelectedBandProfile; onClose: () => void; onDeselect: () => void }) {
  const { t } = useTranslation();
  return (
    <div style={s.bandProfilePanel} data-testid="sidebar-band-profile">
      <Link to={Routes.band(band.bandId, { names: band.displayName })} onClick={onClose} style={s.bandProfileLink}>
        <span style={s.sidebarLinkIcon}><IoPeople size={20} /></span>
        <MarqueeText as="p" text={band.displayName} style={s.bandProfileName} />
      </Link>
      <div style={s.bandProfileType}>{formatBandType(band.bandType)}</div>
      <div style={s.bandMemberList} aria-label={t('band.members')}>
        {band.members.map(member => (
          <Link key={member.accountId} to={Routes.player(member.accountId)} onClick={onClose} style={s.bandMemberLink}>
            <span style={s.sidebarLinkIcon}><IoPerson size={18} /></span>
            <MarqueeText as="p" text={member.displayName} style={s.bandMemberName} />
          </Link>
        ))}
      </div>
      <button style={s.bandDeselectBtn} onClick={onDeselect}>
        {t('band.deselectProfile')}
      </button>
    </div>
  );
}

function formatBandType(bandType: SelectedBandProfile['bandType']): string {
  switch (bandType) {
    case 'Band_Duets': return 'Duos';
    case 'Band_Trios': return 'Trios';
    case 'Band_Quad': return 'Quads';
  }
}
