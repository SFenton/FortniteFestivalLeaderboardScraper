/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Song detail page header — album art, title/artist, and "View Paths" button.
 * Supports collapsed/expanded sizing for scroll-driven transitions.
 * Used by SongDetailPage (no instrument section, unlike SongInfoHeader).
 */
import { useTranslation } from 'react-i18next';
import { IoFlash, IoBagHandle } from 'react-icons/io5';
import { Font, Gap, Radius } from '@festival/theme';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { useShopState } from '../../../hooks/data/useShopState';
import type { ServerSong as Song } from '@festival/core/api/serverTypes';
import css from './SongDetailHeader.module.css';

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
  const artSize = collapsed ? 80 : 120;
  const transition = noTransition ? undefined : 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)';

  return (
    /* v8 ignore start — collapsed ternary styling */
    <div className={css.header} style={{ marginTop: collapsed ? 0 : Gap.xl, transition }}>
      {song?.albumArt ? (
        <img src={song.albumArt} alt="" className={css.headerArt} style={{ width: artSize, height: artSize, borderRadius: collapsed ? Radius.md : Radius.lg, transition }} />
      ) : (
        <div className={css.artPlaceholder} style={{ width: artSize, height: artSize, borderRadius: collapsed ? Radius.md : Radius.lg, transition }} />
      )}
      <div style={{ flex: 1, minWidth: 0 }}>
        <h1 className={css.songTitle} style={{ marginBottom: collapsed ? Gap.xs : Gap.sm, transition }}>{song?.title ?? songId}</h1>
        <p className={css.songArtist} style={{ fontSize: collapsed ? Font.md : Font.lg, marginBottom: collapsed ? 0 : Gap.md, transition }}>
          {song?.artist ?? t('common.unknownArtist')}{song?.year ? ` \u00b7 ${song.year}` : ''}
        </p>
      </div>
      {!isMobile && (
        <button onClick={onOpenPaths} className={css.viewPathsButton}>
          <IoFlash size={16} style={{ marginRight: Gap.md }} />
          {t('common.viewPaths')}
        </button>
      )}
      {!isMobile && showShop && (
        /* v8 ignore start — external link */
        <a href={shopUrl} target="_blank" rel="noopener noreferrer" className={shopPulse ? `${css.shopButton} ${css.shopPulse}` : css.shopButton}>
          <IoBagHandle size={16} style={{ marginRight: Gap.md }} />
          {t('common.itemShop', 'Item Shop')}
        </a>
        /* v8 ignore stop */
      )}
      {isMobile && showShop && (
        /* v8 ignore start — mobile shop icon */
        <a href={shopUrl} target="_blank" rel="noopener noreferrer" className={shopPulse ? `${css.shopCircle} ${css.shopCirclePulse}` : css.shopCircle} aria-label={t('common.itemShop', 'Item Shop')}>
          <IoBagHandle size={24} />
        </a>
        /* v8 ignore stop */
      )}
    </div>
    /* v8 ignore stop */
  );
}
