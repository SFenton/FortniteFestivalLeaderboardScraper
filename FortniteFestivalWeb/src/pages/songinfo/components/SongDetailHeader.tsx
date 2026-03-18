/**
 * Song detail page header — album art, title/artist, and "View Paths" button.
 * Supports collapsed/expanded sizing for scroll-driven transitions.
 * Used by SongDetailPage (no instrument section, unlike SongInfoHeader).
 */
import { useTranslation } from 'react-i18next';
import { IoFlash } from 'react-icons/io5';
import { Font, Gap, Radius } from '@festival/theme';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
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
    </div>
    /* v8 ignore stop */
  );
}
