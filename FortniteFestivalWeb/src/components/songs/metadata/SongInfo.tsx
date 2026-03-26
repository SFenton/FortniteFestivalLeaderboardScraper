/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * Shared song info block: album art thumbnail + title + artist · year.
 * Used across song rows, player song rows, and suggestion cards.
 */
import { memo, useMemo } from 'react';
import { Colors, Font, Gap, Size, Weight, truncate, flexColumn } from '@festival/theme';
import AlbumArt from './AlbumArt';

export interface SongInfoProps {
  albumArt?: string;
  title: string;
  artist: string;
  year?: number;
}

const SongInfo = memo(function SongInfo({ albumArt, title, artist, year }: SongInfoProps) {
  const s = useStyles();
  return (
    <>
      <AlbumArt src={albumArt} size={Size.thumb} />
      <div style={s.text}>
        <span style={s.title}>{title}</span>
        <span style={s.artist}>{artist}{year ? ` \u00b7 ${year}` : ''}</span>
      </div>
    </>
  );
});

export default SongInfo;

function useStyles() {
  return useMemo(() => ({
    text: {
      ...flexColumn,
      gap: Gap.xs,
      minWidth: 0,
      flex: 1,
    },
    title: {
      ...truncate,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
    },
    artist: {
      ...truncate,
      fontSize: Font.sm,
      color: Colors.textSubtle,
    },
  }), []);
}
