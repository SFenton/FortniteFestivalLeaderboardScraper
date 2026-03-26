/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useNavigate } from 'react-router-dom';
import { IoChevronBack } from 'react-icons/io5';
import { InstrumentIcon } from '../../display/InstrumentIcons';
import BackLink from './BackLink';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import {
  Colors, Font, Weight, Gap, Layout, MaxWidth, ZIndex, InstrumentSize,
  Display, Align, Position, WhiteSpace, BoxSizing, CssValue, CssProp,
  flexRow, padding, transition, TRANSITION_MS,
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
}

export default function MobileHeader({
  navTitle,
  backFallback,
  shouldAnimate,
  locationKey,
  songInstrument,
  isSongsRoute,
}: MobileHeaderProps) {
  const navigate = useNavigate();
  const s = useStyles();

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
            <IoChevronBack size={InstrumentSize.sm} />
            <span>{navTitle}</span>
          </a>
        ) : (
          <span style={s.title}>{navTitle}</span>
        )}
        {isSongsRoute && songInstrument && (
          <InstrumentIcon instrument={songInstrument} size={InstrumentSize.sm} style={{ marginLeft: CssValue.auto }} />
        )}
      </div>
    );
  }

  if (backFallback) {
    return <BackLink key={locationKey} fallback={backFallback} animate={shouldAnimate} />;
  }

  return null;
  /* v8 ignore stop */
}

function useStyles() {
  return useMemo(() => ({
    header: {
      ...flexRow,
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
      lineHeight: `${InstrumentSize.sm}px`,
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
      marginLeft: -Gap.sm,
      lineHeight: `${InstrumentSize.sm}px`,
    } as CSSProperties,
  }), []);
}
