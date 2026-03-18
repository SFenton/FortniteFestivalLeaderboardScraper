/**
 * Shared song info block: album art thumbnail + title + artist · year.
 * Used across song rows, player song rows, and suggestion cards.
 */
import { memo } from 'react';
import { Size } from '@festival/theme';
import AlbumArt from './AlbumArt';
import css from './SongInfo.module.css';

export interface SongInfoProps {
  albumArt?: string;
  title: string;
  artist: string;
  year?: number;
}

const SongInfo = memo(function SongInfo({ albumArt, title, artist, year }: SongInfoProps) {
  return (
    <>
      <AlbumArt src={albumArt} size={Size.thumb} />
      <div className={css.text}>
        <span className={css.title}>{title}</span>
        <span className={css.artist}>{artist}{year ? ` \u00b7 ${year}` : ''}</span>
      </div>
    </>
  );
});

export default SongInfo;
