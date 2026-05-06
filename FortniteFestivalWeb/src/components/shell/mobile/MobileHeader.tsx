/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { IoChevronBack, IoSearch } from 'react-icons/io5';
import { InstrumentIcon } from '../../display/InstrumentIcons';
import HamburgerButton from '../HamburgerButton';
import BackLink from './BackLink';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import {
  Colors, Font, Weight, Gap, Layout, MaxWidth, ZIndex, InstrumentSize, IconSize,
  Display, Align, Justify, Position, WhiteSpace, BoxSizing, CssValue, Size, Radius,
  flexRow, flexCenter, padding, TRANSITION_MS,
} from '@festival/theme';

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
}: MobileHeaderProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const s = useStyles();
  const searchAction = (
    <button
      type="button"
      style={s.iconButton}
      onClick={onOpenSearch}
      aria-label={t('common.searchAction')}
    >
      <IoSearch size={IconSize.nav} />
    </button>
  );

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
            {onOpenSidebar && <HamburgerButton onClick={onOpenSidebar} size={IconSize.nav} style={{ marginLeft: Layout.headerIconNudge }} />}
            <span style={s.title}>{navTitle}</span>
          </>
        )}
        <div style={s.rightActions}>
          {isSongsRoute && songInstrument && (
            <InstrumentIcon instrument={songInstrument} size={InstrumentSize.sm} style={s.instrumentIcon} />
          )}
          {searchAction}
        </div>
      </div>
    );
  }

  if (backFallback) {
    return <BackLink key={locationKey} fallback={backFallback} animate={shouldAnimate} rightAction={<span style={s.backLinkRightAction}>{searchAction}</span>} />;
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
    rightActions: {
      ...flexRow,
      alignItems: Align.center,
      gap: Gap.sm,
      marginLeft: CssValue.auto,
      flexShrink: 0,
    } as CSSProperties,
    instrumentIcon: {
      flexShrink: 0,
    } as CSSProperties,
    iconButton: {
      ...flexCenter,
      width: Size.iconMd,
      height: Size.iconMd,
      background: 'none',
      border: 'none',
      cursor: 'pointer',
      borderRadius: Radius.xs,
      color: Colors.textSecondary,
      padding: 0,
      flexShrink: 0,
    } as CSSProperties,
    backLinkRightAction: {
      marginLeft: CssValue.auto,
      flexShrink: 0,
    } as CSSProperties,
  }), []);
}
