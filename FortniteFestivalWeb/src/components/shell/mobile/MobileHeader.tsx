/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useNavigate } from 'react-router-dom';
import { IoChevronBack } from 'react-icons/io5';
import { InstrumentIcon } from '../../display/InstrumentIcons';
import HamburgerButton from '../HamburgerButton';
import HeaderActions, { type HeaderActionProfileType, type HeaderNotificationVisualState } from '../HeaderActions';
import BackLink from './BackLink';
import MarqueeText from '../../common/MarqueeText';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import {
  Colors, Font, Weight, Gap, Layout, MaxWidth, ZIndex, InstrumentSize, IconSize,
  Display, Align, Justify, Position, WhiteSpace, BoxSizing, CssValue,
  flexRow, padding, TRANSITION_MS,
} from '@festival/theme';

export type MobileHeaderProfileType = HeaderActionProfileType;

export interface MobileHeaderProps {
  navTitle: string | null;
  backFallback: string | null;
  shouldAnimate: boolean;
  locationKey: string;
  /** Current instrument filter (shown as icon on /songs). */
  songInstrument: InstrumentKey | null;
  /** Whether we're on the /songs route. */
  isSongsRoute: boolean;
  /** Callback to open the navigation sidebar (shown on root pages). */
  onOpenSidebar?: () => void;
  /** Callback to open the unified search modal. */
  onOpenSearch?: () => void;
  /** Callback to open the notifications modal. */
  onOpenNotifications?: () => void;
  /** Whether the selected profile/band has any notifications in its feed. */
  hasNotifications?: boolean;
  /** Number of active notifications for the mobile bell badge. */
  notificationCount?: number;
  /** Visual state for in-place notification icon/spinner swaps. */
  notificationVisualState?: HeaderNotificationVisualState;
  /** Selected profile state rendered immediately before Search. */
  profileType?: MobileHeaderProfileType;
  /** Accessible label for the selected-profile action. */
  profileLabel?: string;
  /** Callback for profile-state action presses. */
  onProfileAction?: () => void;
}

export default function MobileHeader({
  navTitle,
  backFallback,
  shouldAnimate,
  locationKey,
  songInstrument,
  isSongsRoute,
  onOpenSidebar,
  onOpenSearch,
  onOpenNotifications,
  hasNotifications,
  notificationCount = 0,
  notificationVisualState,
  profileType = 'none',
  profileLabel,
  onProfileAction,
}: MobileHeaderProps) {
  const navigate = useNavigate();
  const s = useStyles();
  const leadingSlot = isSongsRoute && songInstrument
    ? <InstrumentIcon instrument={songInstrument} size={InstrumentSize.sm} style={s.instrumentIcon} />
    : null;
  const rightActionGroup = <HeaderActions
    testIdPrefix="mobile-header"
    leadingSlot={leadingSlot}
    profileType={profileType}
    profileLabel={profileLabel}
    onProfileAction={onProfileAction}
    onOpenSearch={onOpenSearch}
    onOpenNotifications={onOpenNotifications}
    hasNotifications={hasNotifications}
    notificationCount={notificationCount}
    notificationVisualState={notificationVisualState}
  />;

  /* v8 ignore start — conditional rendering tested via AppMobile integration */
  if (navTitle) {
    return (
      <div key={locationKey} className="sa-top" style={{ ...s.header, ...(shouldAnimate ? { animation: `fadeIn ${TRANSITION_MS}ms ease-out` } : undefined) }}>
        {backFallback ? (
          <a
            href="#"
            /* v8 ignore start */
            onClick={(e) => { e.preventDefault(); navigate(-1); }}
            /* v8 ignore stop */
            style={s.titleBack}
          >
            <span style={s.iconSlot}><IoChevronBack size={IconSize.back} /></span>
            <span>{navTitle}</span>
          </a>
        ) : (
          <>
            {onOpenSidebar && <HamburgerButton onClick={onOpenSidebar} size={IconSize.nav} style={{ marginLeft: Layout.headerIconNudge, color: Colors.textPrimary }} />}
            <MarqueeText text={navTitle} style={s.title} overflowInset={6} />
          </>
        )}
        {rightActionGroup}
      </div>
    );
  }

  if (backFallback) {
    return <BackLink key={locationKey} fallback={backFallback} animate={shouldAnimate} rightAction={rightActionGroup} />;
  }

  return null;
  /* v8 ignore stop */
}

function useStyles() {
  return useMemo(() => ({
    header: {
      ...flexRow,
      gap: Gap.sm,
      padding: padding(Layout.paddingTop + Gap.md, Layout.paddingHorizontal, Gap.md),
      maxWidth: MaxWidth.card,
      margin: CssValue.marginCenter,
      width: CssValue.full,
      boxSizing: BoxSizing.borderBox,
      flexShrink: 0,
      zIndex: ZIndex.popover,
      position: Position.relative,
      touchAction: CssValue.none,
    } as CSSProperties,
    title: {
      flex: 1,
      minWidth: 0,
      fontSize: Font.title,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
      whiteSpace: WhiteSpace.nowrap,
      lineHeight: 1,
    } as CSSProperties,
    titleBack: {
      display: Display.inlineFlex,
      alignItems: Align.center,
      gap: Gap.sm,
      fontSize: Font.title,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
      textDecoration: CssValue.none,
      whiteSpace: WhiteSpace.nowrap,
      marginLeft: Layout.headerIconNudge,
      lineHeight: 1,
    } as CSSProperties,
    iconSlot: {
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      width: Layout.headerIconSlot,
      height: Layout.headerIconSlot,
      flexShrink: 0,
    } as CSSProperties,
    instrumentIcon: {
      flexShrink: 0,
    } as CSSProperties,
  }), []);
}
