/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Shared song header bar used by LeaderboardPage, PlayerHistoryPage,
 * and SongDetailPage. Shows album art, song title/artist, and optionally an
 * instrument icon, "View Paths" button, and Item Shop link.
 * Supports collapsed/expanded sizing for scroll-driven transitions.
 */
import { useMemo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { IoFlash, IoBagHandle } from 'react-icons/io5';
import {
  Colors, Font, Gap, Radius, Layout, Weight, ObjectFit, Size, AlbumArtSize,
  IconSize, InstrumentSize, Display, Align, Justify, CssValue, Cursor, Position, Isolation,
  flexRow, flexCenter, padding, purpleGlass,
} from '@festival/theme';
import type { CSSProperties } from 'react';
import { type ServerSong as Song, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../../display/InstrumentIcons';
import BackgroundImage from '../../page/BackgroundImage';
import PageHeader from '../../common/PageHeader';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import anim from '../../../styles/animations.module.css';
import cls from './SongInfoHeader.module.css';



export interface SongInfoHeaderProps {
  /** Song data (may be undefined while loading). */
  song: Song | undefined;
  /** Fallback song ID for title when song is not yet loaded. */
  songId: string;
  /** Whether the header is in collapsed (mobile/scrolled) state. */
  collapsed: boolean;
  /** Instrument to show on the right side. Omit to hide the instrument section. */
  instrument?: ServerInstrumentKey;
  /** Extra controls rendered in the right section (e.g. sort button). */
  actions?: ReactNode;
  /** Enable smooth CSS transitions for collapse animation. */
  animate?: boolean;
  /** When set, renders a "View Paths" pill button (desktop only). */
  onOpenPaths?: () => void;
  /** When set, renders an Item Shop button/circle linking to this URL. */
  shopUrl?: string;
  /** When true, the shop button pulses to draw attention. */
  shopPulse?: boolean;
  /** Skip rendering BackgroundImage (caller renders it separately). */
  hideBackground?: boolean;
  /** Extra inline styles on the outer PageHeader wrapper. */
  style?: CSSProperties;
}

export default function SongInfoHeader({
  song,
  songId,
  collapsed,
  instrument,
  actions,
  animate,
  onOpenPaths,
  shopUrl,
  shopPulse,
  hideBackground,
  style: extraStyle,
}: SongInfoHeaderProps) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const s = useStyles(collapsed, animate);
  const showShop = !!shopUrl;

  return (
    <>
      {!hideBackground && <BackgroundImage src={song?.albumArt} />}
      <PageHeader
        className={animate ? cls.wrapperPadding : undefined}
        style={{
          ...(!animate ? { paddingTop: collapsed ? Gap.md : Layout.paddingTop } : undefined),
          paddingBottom: Gap.section,
          position: 'relative',
          zIndex: 'var(--z-dropdown)',
          flexShrink: 0,
          ...extraStyle,
        } as React.CSSProperties}
        title={
          <div style={s.headerLeft}>
            {song?.albumArt ? (
              <img src={song.albumArt} alt="" className={s.artClassName} style={s.headerArt} />
            ) : (
              <div className={s.artClassName} style={s.artPlaceholder} />
            )}
            <div style={s.textWrap}>
              <h1 className={s.titleClassName} style={s.songTitle}>{song?.title ?? songId}</h1>
              <p className={s.artistClassName} style={s.songArtist}>
                {song?.artist ?? t('common.unknownArtist')}
                {song?.year ? ` \u00b7 ${song.year}` : ''}
              </p>
            </div>
          </div>
        }
        actions={(instrument || actions || onOpenPaths || showShop) ? (
          <>
            {instrument && (
              <div style={s.instIconWrap}>
                <InstrumentIcon instrument={instrument} size={48} />
              </div>
            )}
            {/* v8 ignore start — desktop-only action buttons */}
            {!isMobile && onOpenPaths && (
              <button onClick={onOpenPaths} style={s.viewPathsButton}>
                <IoFlash size={IconSize.action} style={{ marginRight: Gap.md }} />
                {t('common.viewPaths')}
              </button>
            )}
            {!isMobile && showShop && (
              <a href={shopUrl} target="_blank" rel="noopener noreferrer" style={shopPulse ? s.shopButtonPulse : s.shopButton} className={shopPulse ? anim.shopBreathe : undefined}>
                <IoBagHandle size={IconSize.action} style={{ marginRight: Gap.md }} />
                {t('common.itemShop', 'Item Shop')}
              </a>
            )}
            {isMobile && showShop && (
              <a href={shopUrl} target="_blank" rel="noopener noreferrer" style={shopPulse ? s.shopCirclePulse : s.shopCircle} className={shopPulse ? anim.shopCircleBreathe : undefined} aria-label={t('common.itemShop', 'Item Shop')}>
                <IoBagHandle size={IconSize.sm} />
              </a>
            )}
            {/* v8 ignore stop */}
            {actions}
          </>
        ) : undefined}
      />
    </>
  );
}

function useStyles(collapsed: boolean, animate?: boolean) {
  return useMemo(() => {
    // When animate is true, size/radius/font/margin are driven by --collapse CSS var
    // via CSS module classes — no inline transitions needed.
    const artSize = animate ? undefined : (collapsed ? AlbumArtSize.collapsed : AlbumArtSize.expanded);
    const artRadius = animate ? undefined : (collapsed ? Radius.md : Radius.lg);
    const buttonBase: CSSProperties = {
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      padding: padding(0, Layout.buttonPaddingH, 0, Gap.section),
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      fontSize: Font.lg,
      fontWeight: Weight.semibold,
      textDecoration: CssValue.none,
      cursor: Cursor.pointer,
      flexShrink: 0,
      alignSelf: Align.center,
      height: Layout.pillButtonHeight,
    };
    const shopButton: CSSProperties = { ...buttonBase, backgroundColor: Colors.accentBlue };
    const pulseBase: CSSProperties = {
      position: Position.relative,
      backgroundColor: Colors.transparent,
      isolation: Isolation.isolate,
    };
    return {
      headerLeft: { ...flexRow, gap: Gap.section, minWidth: 0 } as CSSProperties,
      headerArt: { width: artSize, height: artSize, borderRadius: artRadius, objectFit: ObjectFit.cover, flexShrink: 0 } as CSSProperties,
      artPlaceholder: { width: artSize, height: artSize, borderRadius: artRadius, backgroundColor: Colors.accentPurpleDark, flexShrink: 0 } as CSSProperties,
      artClassName: animate ? cls.art : undefined,
      textWrap: { flex: 1, minWidth: 0 } as CSSProperties,
      songTitle: { fontSize: Font.title, fontWeight: Weight.bold, margin: 0, marginBottom: animate ? undefined : (collapsed ? Gap.xs : Gap.sm) } as CSSProperties,
      titleClassName: animate ? cls.titleMargin : undefined,
      songArtist: { fontSize: animate ? undefined : (collapsed ? Font.md : Font.lg), color: Colors.textSubtle, margin: 0 } as CSSProperties,
      artistClassName: animate ? cls.artistFont : undefined,
      instIconWrap: { ...flexCenter, width: Size.iconXl, height: Size.iconXl } as CSSProperties,
      viewPathsButton: { ...purpleGlass, ...buttonBase } as CSSProperties,
      shopButton,
      shopButtonPulse: { ...shopButton, ...pulseBase } as CSSProperties,
      shopCircle: {
        width: InstrumentSize.lg,
        height: InstrumentSize.lg,
        borderRadius: Radius.full,
        backgroundColor: Colors.accentBlue,
        ...flexCenter,
        color: Colors.textPrimary,
        textDecoration: CssValue.none,
        flexShrink: 0,
        alignSelf: Align.center,
      } as CSSProperties,
      shopCirclePulse: {
        width: InstrumentSize.lg,
        height: InstrumentSize.lg,
        borderRadius: Radius.full,
        ...flexCenter,
        color: Colors.textPrimary,
        textDecoration: CssValue.none,
        flexShrink: 0,
        alignSelf: Align.center,
        ...pulseBase,
      } as CSSProperties,
    };
  }, [collapsed, animate]);
}
