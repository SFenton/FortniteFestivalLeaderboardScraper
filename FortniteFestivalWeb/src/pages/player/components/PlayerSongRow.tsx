import { memo } from 'react';
import { formatPercentileBucket } from '@festival/core';
import SongInfo from '../../../components/songs/metadata/SongInfo';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';
import s from './PlayerSongRow.module.css';

export interface PlayerSongRowProps {
  songId: string;
  href: string;
  albumArt?: string;
  title: string;
  artist: string;
  year?: number;
  percentile?: number;
  onClick: (e: React.MouseEvent) => void;
}

const PlayerSongRow = memo(function PlayerSongRow({
  songId,
  href,
  albumArt,
  title,
  artist,
  year,
  percentile,
  onClick,
}: PlayerSongRowProps) {
  return (
    <a key={songId} href={href} onClick={onClick} className={s.songListRow}>
      <SongInfo albumArt={albumArt} title={title} artist={artist} year={year} />
      <div className={s.topSongRight}>
        {percentile != null && (
          <PercentilePill
            display={formatPercentileBucket(percentile)}
          />
        )}
      </div>
    </a>
  );
});

export default PlayerSongRow;
