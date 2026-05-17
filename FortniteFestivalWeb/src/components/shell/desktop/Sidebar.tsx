/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useLayoutEffect, useState, useCallback, useRef, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent } from 'react';
import { NavLink, Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoCompass, IoPerson, IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings, IoBagHandle, IoPeople, IoTrophy } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import type { SelectedBandProfile, SelectedProfile } from '../../../hooks/data/useSelectedProfile';
import { useSettings } from '../../../contexts/SettingsContext';
import MarqueeText from '../../common/MarqueeText';
import PressableButton from '../../common/PressableButton';
import { sidebarStyles as s } from './sidebarStyles';
import { Routes } from '../../../routes';
import { getStatisticsNavigationPath } from '../../../utils/profileNavigation';
import { markTapDiagnosticsAction } from '../../../diagnostics/tapDiagnostics';
import { usePressAction } from '../../../hooks/ui/usePressAction';
import { useFeatureFlags } from '../../../contexts/FeatureFlagsContext';

const SIDEBAR_DURATION = 250;
const TOUCH_NAV_MOVEMENT_THRESHOLD = 12;

type SidebarNavHandlers = {
  onPointerDown: (event: ReactPointerEvent<HTMLAnchorElement>) => void;
  onPointerUp: (event: ReactPointerEvent<HTMLAnchorElement>) => void;
  onPointerCancel: () => void;
  onClick: (event: ReactMouseEvent<HTMLAnchorElement>) => void;
};

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
  const { appManual } = useFeatureFlags();
  const navigate = useNavigate();
  const sidebarRef = useRef<HTMLDivElement>(null);
  const pendingTouchNavRef = useRef<{ pointerId: number; label: string; to: string; clientX: number; clientY: number } | null>(null);
  const [mounted, setMounted] = useState(false);
  const [visible, setVisible] = useState(false);
  const [instantDismissed, setInstantDismissed] = useState(false);
  const rendered = !instantDismissed && (open || mounted);

  useEffect(() => {
    if (open) {
      setInstantDismissed(false);
      setMounted(true);
    } else {
      setVisible(false);
    }
  }, [open]);

  const handleNavigationLink = useCallback((label: string, to: string, trigger: 'click' | 'pointerup') => {
    markTapDiagnosticsAction('nav:start', 'start', { source: 'sidebar', trigger, label, to });
    setInstantDismissed(true);
    setVisible(false);
    setMounted(false);
    onClose();
    navigate(to);
  }, [navigate, onClose]);

  const getNavigationHandlers = useCallback((label: string, to: string): SidebarNavHandlers => ({
    onPointerDown: (event) => {
      if (event.button !== 0 || event.pointerType === 'mouse') return;
      pendingTouchNavRef.current = { pointerId: event.pointerId, label, to, clientX: event.clientX, clientY: event.clientY };
    },
    onPointerUp: (event) => {
      const pending = pendingTouchNavRef.current;
      pendingTouchNavRef.current = null;
      if (!pending || pending.pointerId !== event.pointerId || pending.label !== label || pending.to !== to) return;

      const moved = Math.hypot(event.clientX - pending.clientX, event.clientY - pending.clientY);
      if (moved > TOUCH_NAV_MOVEMENT_THRESHOLD) return;

      event.preventDefault();
      handleNavigationLink(label, to, 'pointerup');
    },
    onPointerCancel: () => {
      pendingTouchNavRef.current = null;
    },
    onClick: (event) => {
      if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      event.preventDefault();
      handleNavigationLink(label, to, 'click');
    },
  }), [handleNavigationLink]);
  const overlayPressHandlers = usePressAction<HTMLDivElement>({ onPress: onClose, disabled: !open });

  /* v8 ignore start -- animation: rAF + getBoundingClientRect */
  useLayoutEffect(() => {
    if (rendered && open) {
      sidebarRef.current?.getBoundingClientRect();
      const id = requestAnimationFrame(() => setVisible(true));
      return () => cancelAnimationFrame(id);
    }
  }, [rendered, open]);

  const handleTransitionEnd = useCallback(() => {
    if (!open) setMounted(false);
  }, [open]);
  /* v8 ignore stop */

  useEffect(() => {
    if (!mounted) return;
    function handleClick(e: MouseEvent) {
      if (sidebarRef.current && !sidebarRef.current.contains(e.target as Node)) onClose();
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [mounted, onClose]);

  if (!rendered) return null;
  const selectedBand = selectedProfile?.type === 'band' ? selectedProfile : null;
  const showSuggestions = !!player || !!selectedBand;
  const statisticsPath = getStatisticsNavigationPath(player, selectedProfile ?? null);
  const sidebarPointerEvents = open ? 'auto' as const : 'none' as const;

  return (
    <>
      <div
        style={{ ...s.overlay, opacity: visible ? 1 : 0, transition: `opacity ${SIDEBAR_DURATION}ms ease`, pointerEvents: sidebarPointerEvents }}
        {...overlayPressHandlers}
      />
      <div
        ref={sidebarRef}
        style={{ ...s.sidebar, transform: visible ? 'translateX(0)' : 'translateX(-100%)', transition: `transform ${SIDEBAR_DURATION}ms ease`, pointerEvents: sidebarPointerEvents }}
        onTransitionEnd={handleTransitionEnd}
      >
        <div style={s.sidebarHeader}>
          <span style={s.brand}>{t('common.brandName')}</span>
        </div>
        <nav style={s.sidebarNav}>
          <NavLink to="/songs" {...getNavigationHandlers(t('nav.songs'), '/songs')} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
            <span style={s.sidebarLinkIcon}><IoMusicalNotes size={20} /></span>
            {t('nav.songs')}
          </NavLink>
          {showSuggestions && (
            <NavLink to="/suggestions" {...getNavigationHandlers(t('nav.suggestions'), '/suggestions')} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              <span style={s.sidebarLinkIcon}><IoSparkles size={20} /></span>
              {t('nav.suggestions')}
            </NavLink>
          )}
          {statisticsPath && (
            <NavLink to={statisticsPath} {...getNavigationHandlers(t('nav.statistics'), statisticsPath)} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              <span style={s.sidebarLinkIcon}><IoStatsChart size={20} /></span>
              {t('nav.statistics')}
            </NavLink>
          )}
          {/* v8 ignore start -- player-gated link */}
          {player && (
            <NavLink to="/rivals" {...getNavigationHandlers(t('nav.rivals', 'Rivals'), '/rivals')} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              <span style={s.sidebarLinkIcon}><IoPeople size={20} /></span>
              {t('nav.rivals', 'Rivals')}
            </NavLink>
          )}
          {/* v8 ignore stop */}
          <NavLink to="/leaderboards" {...getNavigationHandlers(t('nav.leaderboards'), '/leaderboards')} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
            <span style={s.sidebarLinkIcon}><IoTrophy size={20} /></span>
            {t('nav.leaderboards')}
          </NavLink>
          {/* v8 ignore start -- shop-visibility link */}
          {!settings.hideItemShop && (
            <NavLink to="/shop" {...getNavigationHandlers(t('nav.shop', 'Shop'), '/shop')} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              <span style={s.sidebarLinkIcon}><IoBagHandle size={20} /></span>
              {t('nav.shop', 'Shop')}
            </NavLink>
          )}
          {/* v8 ignore stop */}
        </nav>
        <div style={s.sidebarFooter}>
          {selectedBand ? (
            <SelectedBandPanel band={selectedBand} getNavigationHandlers={getNavigationHandlers} onDeselect={onDeselect} />
          ) : player ? (
            <div style={s.sidebarPlayerRow}>
              <Link to="/statistics" {...getNavigationHandlers(player.displayName, '/statistics')} style={s.playerLink}>
                <span style={s.sidebarLinkIcon}><IoPerson size={20} /></span>
                <MarqueeText as="p" text={player.displayName} style={s.playerName} />
              </Link>
              <PressableButton style={s.deselectBtn} onPress={onDeselect}>
                {t('common.deselect')}
              </PressableButton>
            </div>
          ) : (
            <PressableButton style={s.selectPlayerBtn} onPress={onSelectPlayer}>
              <span style={s.sidebarLinkIcon}><IoPerson size={20} /></span>
              {t('common.selectProfile')}
            </PressableButton>
          )}
          {appManual && (
            <NavLink to={Routes.manual} {...getNavigationHandlers(t('nav.manual'), Routes.manual)} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
              <span style={s.sidebarLinkIcon}><IoCompass size={20} /></span>
              {t('nav.manual')}
            </NavLink>
          )}
          <NavLink to="/settings" {...getNavigationHandlers(t('nav.settings'), '/settings')} style={({ isActive }) => isActive ? s.sidebarLinkActive : s.sidebarLink}>
            <span style={s.sidebarLinkIcon}><IoSettings size={20} /></span>
            {t('nav.settings')}
          </NavLink>
        </div>
      </div>
    </>
  );
}

function SelectedBandPanel({ band, getNavigationHandlers, onDeselect }: { band: SelectedBandProfile; getNavigationHandlers: (label: string, to: string) => SidebarNavHandlers; onDeselect: () => void }) {
  const { t } = useTranslation();
  return (
    <div style={s.bandProfilePanel} data-testid="sidebar-band-profile">
      <Link to={Routes.statistics} {...getNavigationHandlers(band.displayName, Routes.statistics)} style={s.bandProfileLink}>
        <span style={s.sidebarLinkIcon}><IoPeople size={20} /></span>
        <MarqueeText as="p" text={band.displayName} style={s.bandProfileName} />
      </Link>
      <div style={s.bandProfileType}>{formatBandType(band.bandType)}</div>
      <div style={s.bandMemberList} aria-label={t('band.members')}>
        {band.members.map(member => (
          <Link key={member.accountId} to={Routes.player(member.accountId)} {...getNavigationHandlers(member.displayName, Routes.player(member.accountId))} style={s.bandMemberLink}>
            <span style={s.sidebarLinkIcon}><IoPerson size={18} /></span>
            <MarqueeText as="p" text={member.displayName} style={s.bandMemberName} />
          </Link>
        ))}
      </div>
      <PressableButton style={s.bandDeselectBtn} onPress={onDeselect}>
        {t('band.deselectProfile')}
      </PressableButton>
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
