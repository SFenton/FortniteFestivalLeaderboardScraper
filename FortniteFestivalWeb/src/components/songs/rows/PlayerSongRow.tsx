/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo } from 'react';
import { formatPercentileBucket } from '@festival/core';
import { frostedCard, flexRow, Gap, Radius } from '@festival/theme';
import SongInfo from '../metadata/SongInfo';
import PercentilePill from '../metadata/PercentilePill';

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
  const st = useStyles();
  return (
    <a key={songId} href={href} onClick={onClick} style={st.songListRow}>
      <SongInfo albumArt={albumArt} title={title} artist={artist} year={year} />
      <div style={st.topSongRight}>
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

function useStyles() {
  return useMemo(() => ({
    songListRow: {
      ...frostedCard,
      ...flexRow,
      gap: Gap.xl,
      padding: `0 ${Gap.xl}px`,
      height: 64,
      borderRadius: Radius.md,
    } as React.CSSProperties,
    topSongRight: {
      ...flexRow,
      gap: Gap.xl,
      flexShrink: 0,
      marginLeft: 'auto',
    } as React.CSSProperties,
  }), []);
}
