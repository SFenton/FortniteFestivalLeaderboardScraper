/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * Song detail page header — album art, title/artist, and "View Paths" button.
 * Supports collapsed/expanded sizing for scroll-driven transitions.
 * Used by SongDetailPage (no instrument section, unlike SongInfoHeader).
 */
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { IoFlash, IoBagHandle } from 'react-icons/io5';
import {
  Align, AlbumArtSize, Colors, CssProp, CssValue, Cursor, Display, Font, Gap,
  IconSize, Isolation, InstrumentSize, Justify, Layout, ObjectFit, Position, Radius,
  TRANSITION_MS, EASE_SMOOTH, Weight, flexCenter, flexRow, padding, purpleGlass, transition,
} from '@festival/theme';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { useShopState } from '../../../hooks/data/useShopState';
import type { ServerSong as Song } from '@festival/core/api/serverTypes';
import anim from '../../../styles/animations.module.css';

export interface SongDetailHeaderProps {
  song: Song | undefined;
  songId: string;
  collapsed: boolean;
  /** Disable CSS transitions (e.g. on mobile where collapse is instant). */
  noTransition?: boolean;
  onOpenPaths: () => void;
}

export default function SongDetailHeader({
  song,
  songId,
  collapsed,
  noTransition,
  onOpenPaths,
}: SongDetailHeaderProps) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const { isShopVisible, isShopHighlighted, getShopUrl } = useShopState();
  /* v8 ignore start -- shop branch coverage depends on ShopContext mock state */
  const shopUrl = song ? getShopUrl(song.songId) : undefined;
  const showShop = isShopVisible && !!shopUrl;
  const shopPulse = showShop && song ? isShopHighlighted(song.songId) : false;
  /* v8 ignore stop */
  const s = useStyles(collapsed, noTransition);

  return (
    /* v8 ignore start — collapsed ternary styling */
    <div style={s.header}>
      {song?.albumArt ? (
        <img src={song.albumArt} alt="" style={s.headerArt} />
      ) : (
        <div style={s.artPlaceholder} />
      )}
      <div style={s.textWrap}>
        <h1 style={s.songTitle}>{song?.title ?? songId}</h1>
        <p style={s.songArtist}>
          {song?.artist ?? t('common.unknownArtist')}{song?.year ? ` \u00b7 ${song.year}` : ''}
        </p>
      </div>
      {!isMobile && (
        <button onClick={onOpenPaths} style={s.viewPathsButton}>
          <IoFlash size={IconSize.action} style={{ marginRight: Gap.md }} />
          {t('common.viewPaths')}
        </button>
      )}
      {!isMobile && showShop && (
        /* v8 ignore start — external link */
        <a href={shopUrl} target="_blank" rel="noopener noreferrer" style={shopPulse ? s.shopButtonPulse : s.shopButton} className={shopPulse ? anim.shopBreathe : undefined}>
          <IoBagHandle size={IconSize.action} style={{ marginRight: Gap.md }} />
          {t('common.itemShop', 'Item Shop')}
        </a>
        /* v8 ignore stop */
      )}
      {isMobile && showShop && (
        /* v8 ignore start — mobile shop icon */
        <a href={shopUrl} target="_blank" rel="noopener noreferrer" style={shopPulse ? s.shopCirclePulse : s.shopCircle} className={shopPulse ? anim.shopCircleBreathe : undefined} aria-label={t('common.itemShop', 'Item Shop')}>
          <IoBagHandle size={IconSize.sm} />
        </a>
        /* v8 ignore stop */
      )}
    </div>
    /* v8 ignore stop */
  );
}

function useStyles(collapsed: boolean, noTransition?: boolean) {
  return useMemo(() => {
    const trans = noTransition ? undefined : transition(CssProp.all, TRANSITION_MS, EASE_SMOOTH);
    const artSize = collapsed ? AlbumArtSize.collapsed : AlbumArtSize.expanded;
    const buttonBase = {
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
    const shopButton = { ...buttonBase, backgroundColor: Colors.accentBlue };
    const pulseBase = {
      position: Position.relative,
      backgroundColor: Colors.transparent,
      isolation: Isolation.isolate,
    };
    return {
      header: { ...flexRow, gap: Gap.section, marginTop: collapsed ? 0 : Gap.xl, transition: trans },
      headerArt: { width: artSize, height: artSize, borderRadius: collapsed ? Radius.md : Radius.lg, objectFit: ObjectFit.cover, flexShrink: 0, transition: trans },
      artPlaceholder: { width: artSize, height: artSize, borderRadius: collapsed ? Radius.md : Radius.lg, backgroundColor: Colors.accentPurpleDark, flexShrink: 0, transition: trans },
      textWrap: { flex: 1, minWidth: 0 },
      songTitle: { fontSize: Font.title, fontWeight: Weight.bold, marginBottom: collapsed ? Gap.xs : Gap.sm, transition: trans },
      songArtist: { fontSize: collapsed ? Font.md : Font.lg, color: Colors.textSubtle, marginBottom: collapsed ? 0 : Gap.md, transition: trans },
      viewPathsButton: { ...purpleGlass, ...buttonBase },
      shopButton,
      shopButtonPulse: { ...shopButton, ...pulseBase },
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
      },
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
      },
    };
  }, [collapsed, noTransition]);
}
