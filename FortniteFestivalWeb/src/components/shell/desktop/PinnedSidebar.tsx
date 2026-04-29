/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { NavLink, Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoPerson, IoMusicalNotes, IoSparkles, IoStatsChart, IoSettings, IoBagHandle, IoPeople, IoTrophy } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import type { SelectedBandProfile, SelectedProfile } from '../../../hooks/data/useSelectedProfile';
import { useSettings } from '../../../contexts/SettingsContext';
import MarqueeText from '../../common/MarqueeText';
import { useScrollContainer } from '../../../contexts/ScrollContainerContext';
import { Routes } from '../../../routes';
import {
  Colors, Font, Weight, Gap, Radius, Border, Layout, ZIndex,
  Display, Align, Justify, Cursor, BoxSizing, CssValue, CssProp,
  flexColumn, flexRow, purpleGlass, btnDanger, transition, transitions, padding, border,
  Overflow, LINK_TRANSITION_MS, PointerEvents,
} from '@festival/theme';

interface PinnedSidebarProps {
  player: TrackedPlayer | null;
  selectedProfile?: SelectedProfile | null;
  onDeselect: () => void;
  onSelectPlayer: () => void;
}

export default function PinnedSidebar({ player, selectedProfile, onDeselect, onSelectPlayer }: PinnedSidebarProps) {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const scrollRef = useScrollContainer();
  const s = useStyles();
  const selectedBand = selectedProfile?.type === 'band' ? selectedProfile : null;

  const linkClass = (isActive: boolean) => isActive ? s.linkActive : s.link;

  return (
    <aside style={s.sidebar} data-testid="pinned-sidebar" onWheel={(e) => { scrollRef.current?.scrollBy({ top: e.deltaY, left: e.deltaX }); }}>
      <nav style={s.nav}>
        <NavLink to="/songs" style={({ isActive }) => linkClass(isActive)}>
          <span style={s.linkIcon}><IoMusicalNotes size={20} /></span>
          {t('nav.songs')}
        </NavLink>
        {player && (
          <NavLink to="/suggestions" style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoSparkles size={20} /></span>
            {t('nav.suggestions')}
          </NavLink>
        )}
        {player && (
          <NavLink to="/statistics" style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoStatsChart size={20} /></span>
            {t('nav.statistics')}
          </NavLink>
        )}
        {/* v8 ignore start -- player-gated link */}
        {player && (
          <NavLink to="/rivals" style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoPeople size={20} /></span>
            {t('nav.rivals', 'Rivals')}
          </NavLink>
        )}
        {/* v8 ignore stop */}
        <NavLink to="/leaderboards" style={({ isActive }) => linkClass(isActive)}>
          <span style={s.linkIcon}><IoTrophy size={20} /></span>
          {t('nav.leaderboards')}
        </NavLink>
        {/* v8 ignore start -- shop-visibility link */}
        {!settings.hideItemShop && (
          <NavLink to="/shop" style={({ isActive }) => linkClass(isActive)}>
            <span style={s.linkIcon}><IoBagHandle size={20} /></span>
            {t('nav.shop', 'Shop')}
          </NavLink>
        )}
        {/* v8 ignore stop */}
        <div style={s.spacer} />
        {selectedBand ? (
          <SelectedBandPanel band={selectedBand} onDeselect={onDeselect} styles={s} />
        ) : player ? (
          <div style={s.playerRow}>
            <Link to="/statistics" style={s.playerLink}>
              <span style={s.linkIcon}><IoPerson size={20} /></span>
              <MarqueeText as="p" text={player.displayName} style={s.playerName} />
            </Link>
            <button style={s.deselectBtn} onClick={onDeselect}>
              {t('common.deselect')}
            </button>
          </div>
        ) : (
          <button style={s.selectPlayerBtn} onClick={onSelectPlayer}>
            <span style={s.linkIcon}><IoPerson size={20} /></span>
            {t('common.selectPlayerProfile')}
          </button>
        )}
        <NavLink to="/settings" style={({ isActive }) => linkClass(isActive)}>
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
      <Link to={Routes.band(band.bandId, { names: band.displayName })} style={s.bandProfileLink}>
        <span style={s.linkIcon}><IoPeople size={20} /></span>
        <MarqueeText as="p" text={band.displayName} style={s.bandProfileName} />
      </Link>
      <div style={s.bandProfileType}>{formatBandType(band.bandType)}</div>
      <div style={s.bandMemberList} aria-label={t('band.members')}>
        {band.members.map(member => (
          <Link key={member.accountId} to={Routes.player(member.accountId)} style={s.bandMemberLink}>
            <span style={s.linkIcon}><IoPerson size={18} /></span>
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
      sidebar: {
        width: Layout.sidebarWidth,
        height: '100%',
        flexShrink: 0,
        ...flexColumn,
        overflow: Overflow.hidden,
        background: CssValue.transparent,
        zIndex: ZIndex.base,
        pointerEvents: PointerEvents.auto,
      } as CSSProperties,
      nav: {
        ...flexColumn,
        flex: 1,
        padding: padding(Gap.md, 0, Gap.md, Gap.md),
        gap: Gap.xs,
      } as CSSProperties,
      spacer: { flex: 1 } as CSSProperties,
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
