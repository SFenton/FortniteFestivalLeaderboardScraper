/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useTranslation } from 'react-i18next';
import { IoFlash } from 'react-icons/io5';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { Font, Gap, Radius, Size } from '@festival/theme';
import { type ServerSong as Song } from '@festival/core/api/serverTypes';
import s from './SongHeader.module.css';

export interface SongHeaderProps {
  song: Song | undefined;
  songId: string;
  collapsed: boolean;
  noTransition?: boolean;
  onOpenPaths: () => void;
}

export default function SongHeader({ song,
  songId,
  collapsed,
  noTransition,
  onOpenPaths,
}: SongHeaderProps) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const artSize = collapsed ? Size.albumArtCollapsed : Size.albumArtExpanded;
  const transition = noTransition ? undefined : 'all 300ms cubic-bezier(0.4, 0, 0.2, 1)';
  return (
    /* v8 ignore start — collapsed ternary styling */
    <div className={s.header} style={{ marginTop: collapsed ? 0 : Gap.xl, transition }}>
      {song?.albumArt ? (
        <img src={song.albumArt} alt="" className={s.headerArt} style={{ width: artSize, height: artSize, borderRadius: collapsed ? Radius.md : Radius.lg, transition }} />
      ) : (
        <div className={s.artPlaceholder} style={{ width: artSize, height: artSize, borderRadius: collapsed ? Radius.md : Radius.lg, transition }} />
      )}
      <div style={{ flex: 1, minWidth: 0 }}>
        <h1 className={s.songTitle} style={{ marginBottom: collapsed ? Gap.xs : Gap.sm, transition }}>{song?.title ?? songId}</h1>
        <p className={s.songArtist} style={{ fontSize: collapsed ? Font.md : Font.lg, marginBottom: collapsed ? 0 : Gap.md, transition }}>
          {song?.artist ?? t('common.unknownArtist')}{song?.year ? ` · ${song.year}` : ''}
        </p>
      </div>
      {!isMobile && (
        <button
          onClick={onOpenPaths}
          className={s.viewPathsButton}
        >
          <IoFlash size={Size.iconXs} style={{ marginRight: Gap.md }} />
          {t('common.viewPaths')}
        </button>
      )}
    </div>
    /* v8 ignore stop */
  );
}
