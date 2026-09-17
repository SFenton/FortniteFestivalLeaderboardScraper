import { useMemo, useRef, type CSSProperties } from 'react';
import { NavLink, Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoCompass, IoPerson, IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings, IoBagHandle, IoPeople, IoTrophy } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import type { SelectedBandProfile, SelectedProfile } from '../../../hooks/data/useSelectedProfile';
import { useSettings } from '../../../contexts/SettingsContext';
import { useFeatureFlags } from '../../../contexts/FeatureFlagsContext';
import MarqueeText from '../../common/MarqueeText';
import PressableButton from '../../common/PressableButton';
import { Routes } from '../../../routes';
import { getStatisticsNavigationPath } from '../../../utils/profileNavigation';
import {
  Colors, Font, Weight, Gap, Radius, Border, Layout,
  Display, Align, Justify, Cursor, BoxSizing, CssValue, CssProp,
  flexColumn, flexRow, purpleGlass, btnDanger, transition, transitions, padding, border,
  Overflow, LINK_TRANSITION_MS,
} from '@festival/theme';
import { bandTypeLabel } from '../../../utils/bandTypes';
import css from './PinnedSidebar.module.css';
import { usePanelWheelHandoff } from '../../../hooks/ui/useWheelHandoff';

interface PinnedSidebarProps {
  player: TrackedPlayer | null;
  selectedProfile?: SelectedProfile | null;
  onDeselect: () => void;
  onSelectPlayer: () => void;
}

export default function PinnedSidebar({ player, selectedProfile, onDeselect, onSelectPlayer }: PinnedSidebarProps) {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const { appManual } = useFeatureFlags();
  const s = useStyles();
  const navigationRef = useRef<HTMLElement>(null);
  const utilitiesRef = useRef<HTMLElement>(null);
  usePanelWheelHandoff(navigationRef);
  usePanelWheelHandoff(utilitiesRef);
  const selectedBand = selectedProfile?.type === 'band' ? selectedProfile : null;
  const showSuggestions = !!player || !!selectedBand;
  const statisticsPath = getStatisticsNavigationPath(player, selectedProfile ?? null);

  const linkClass = (isActive: boolean) => isActive ? s.linkActive : s.link;

  return (
    <aside className={css.frame} data-testid="pinned-sidebar">
      <nav ref={navigationRef} className={css.panel} aria-label={t('nav.mainNavigation')} data-testid="pinned-navigation">
        <NavLink to={Routes.songs} style={({ isActive }) => linkClass(isActive)}>
          <span style={s.linkIcon}><IoMusicalNotes size={20} /></span>
          {t('nav.songs')}
        </NavLink>
        {showSuggestions && (
          <NavLink to={Routes.suggestions} style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoSparkles size={20} /></span>
            {t('nav.suggestions')}
          </NavLink>
        )}
        {statisticsPath && (
          <NavLink to={statisticsPath} style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoStatsChart size={20} /></span>
            {t('nav.statistics')}
          </NavLink>
        )}
        {/* v8 ignore start -- player-gated link */}
        {player && (
          <NavLink to={Routes.rivals} style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoPeople size={20} /></span>
            {t('nav.rivals', 'Rivals')}
          </NavLink>
        )}
        {/* v8 ignore stop */}
        <NavLink to={Routes.leaderboards} style={({ isActive }) => linkClass(isActive)}>
          <span style={s.linkIcon}><IoTrophy size={20} /></span>
          {t('nav.leaderboards')}
        </NavLink>
        {/* v8 ignore start -- shop-visibility link */}
        {!settings.hideItemShop && (
          <NavLink to={Routes.shop} style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoBagHandle size={20} /></span>
            {t('nav.shop', 'Shop')}
          </NavLink>
        )}
        {/* v8 ignore stop */}
      </nav>
      <nav ref={utilitiesRef} className={css.panel} aria-label={t('nav.profileSettings')} data-testid="pinned-utilities">
        {selectedBand ? (
          <SelectedBandPanel band={selectedBand} onDeselect={onDeselect} styles={s} />
        ) : player ? (
          <div style={s.playerRow}>
            <Link to={Routes.statistics} style={s.playerLink}>
              <span style={s.linkIcon}><IoPerson size={20} /></span>
              <MarqueeText as="p" text={player.displayName} style={s.playerName} />
            </Link>
            <PressableButton style={s.deselectBtn} onPress={onDeselect}>
              {t('common.deselect')}
            </PressableButton>
          </div>
        ) : (
          <PressableButton style={s.selectPlayerBtn} onPress={onSelectPlayer}>
            <span style={s.linkIcon}><IoPerson size={20} /></span>
            {t('common.selectProfile')}
          </PressableButton>
        )}
        {appManual && (
          <NavLink to={Routes.manual} style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoCompass size={20} /></span>
            {t('nav.manual')}
          </NavLink>
        )}
        <NavLink to={Routes.settings} style={({ isActive }) => linkClass(isActive)}>
          <span style={s.linkIcon}><IoSettings size={20} /></span>
          {t('nav.settings')}
        </NavLink>
      </nav>
    </aside>
  );
}

function SelectedBandPanel({ band, onDeselect, styles: s }: { band: SelectedBandProfile; onDeselect: () => void; styles: ReturnType<typeof useStyles> }) {
  const { t } = useTranslation();
  return (
    <div style={s.bandProfilePanel} data-testid="pinned-sidebar-band-profile">
      <Link to={Routes.statistics} style={s.bandProfileLink}>
        <span style={s.linkIcon}><IoPeople size={20} /></span>
        <MarqueeText as="p" text={band.displayName} style={s.bandProfileName} />
      </Link>
      <div style={s.bandProfileType}>{bandTypeLabel(band.bandType, t)}</div>
      <div style={s.bandMemberList} aria-label={t('band.members')}>
        {band.members.map(member => (
          <Link key={member.accountId} to={Routes.player(member.accountId)} style={s.bandMemberLink}>
            <span style={s.linkIcon}><IoPerson size={18} /></span>
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

/** Exported styles for NavigationDemo cross-consumer. */
export { useStyles as usePinnedSidebarStyles };

function useStyles() {
  return useMemo(() => {
    const bgTransition = transitions(
      transition(CssProp.backgroundColor, LINK_TRANSITION_MS),
      transition(CssProp.borderColor, LINK_TRANSITION_MS),
      transition(CssProp.boxShadow, LINK_TRANSITION_MS),
      transition(CssProp.color, LINK_TRANSITION_MS),
    );
    const link: CSSProperties = {
      ...flexRow,
      gap: Gap.lg,
      height: Layout.entryRowHeight,
      padding: padding(0, Gap.xl),
      color: Colors.textPrimary,
      textDecoration: CssValue.none,
      fontSize: Font.lg,
      fontWeight: Weight.semibold,
      borderRadius: Radius.full,
      boxSizing: BoxSizing.borderBox,
      backgroundColor: CssValue.transparent,
      border: border(Border.thin, CssValue.transparent),
      boxShadow: CssValue.none,
      transition: bgTransition,
      '--frosted-card': '1',
    } as CSSProperties;
    return {
      nav: {
        ...flexColumn,
        flex: 1,
        padding: padding(Gap.md, 0, Gap.md, Gap.md),
        gap: Gap.xs,
      } as CSSProperties,
      link,
      linkActive: {
        ...link,
        ...purpleGlass,
        boxShadow: CssValue.none,
        color: Colors.textPrimary,
      } as CSSProperties,
      linkIcon: {
        ...flexRow,
        flexShrink: 0,
        width: 20,
        justifyContent: Justify.center,
      } as CSSProperties,
      playerRow: {
        display: Display.flex,
        alignItems: Align.center,
      } as CSSProperties,
      playerLink: {
        ...link,
        flex: 1,
        minWidth: 0,
        overflow: Overflow.hidden,
      } as CSSProperties,
      playerName: {
        flex: 1,
        minWidth: 0,
      } as CSSProperties,
      bandProfilePanel: {
        ...flexColumn,
        gap: Gap.sm,
        padding: padding(0, Gap.md),
      } as CSSProperties,
      bandProfileLink: {
        ...link,
        minWidth: 0,
        overflow: Overflow.hidden,
        padding: padding(0, Gap.md),
      } as CSSProperties,
      bandProfileName: {
        flex: 1,
        minWidth: 0,
      } as CSSProperties,
      bandProfileType: {
        color: Colors.textSubtle,
        fontSize: Font.sm,
        fontWeight: Weight.semibold,
        padding: padding(0, Gap.md),
        marginTop: -Gap.xs,
      } as CSSProperties,
      bandMemberList: {
        ...flexColumn,
        gap: Gap.xs,
      } as CSSProperties,
      bandMemberLink: {
        ...link,
        height: 36,
        minWidth: 0,
        overflow: Overflow.hidden,
        padding: padding(0, Gap.md),
        fontSize: Font.sm,
      } as CSSProperties,
      bandMemberName: {
        flex: 1,
        minWidth: 0,
      } as CSSProperties,
      bandDeselectBtn: {
        ...btnDanger,
        alignSelf: 'flex-start',
        padding: padding(Gap.sm, Gap.xl),
        fontSize: Font.sm,
        whiteSpace: 'nowrap',
        marginLeft: Gap.md,
      } as CSSProperties,
      selectPlayerBtn: {
        ...link,
        background: CssValue.none,
        cursor: Cursor.pointer,
      } as CSSProperties,
      deselectBtn: {
        ...btnDanger,
        padding: padding(Gap.sm, Gap.xl),
        fontSize: Font.sm,
        whiteSpace: 'nowrap',
        marginLeft: 'auto',
      } as CSSProperties,
    };
  }, []);
}
