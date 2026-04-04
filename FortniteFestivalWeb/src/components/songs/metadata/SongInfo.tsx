/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * Shared song info block: album art thumbnail + title + artist · year.
 * Used across song rows, player song rows, and suggestion cards.
 */
import { memo, useMemo } from 'react';
import { Colors, Font, Gap, Size, Weight, flexColumn } from '@festival/theme';
import AlbumArt from './AlbumArt';
import MarqueeText from '../../common/MarqueeText';

export interface SongInfoProps {
  albumArt?: string;
  title: string;
  artist: string;
  year?: number;
  minWidth?: number;
}

const SongInfo = memo(function SongInfo({ albumArt, title, artist, year, minWidth }: SongInfoProps) {
  const s = useStyles(minWidth);
  const artistText = `${artist}${year ? ` \u00b7 ${year}` : ''}`;
  return (
    <>
      <AlbumArt src={albumArt} size={Size.thumb} />
      <div style={s.text}>
        <MarqueeText text={title} as="p" style={s.title} />
        <MarqueeText text={artistText} as="p" style={s.artist} />
      </div>
    </>
  );
});

export default SongInfo;

function useStyles(minWidth?: number) {
  return useMemo(() => ({
    text: {
      ...flexColumn,
      gap: Gap.xs,
      minWidth: minWidth ?? 200,
      flex: 1,
      overflow: 'hidden',
    },
    title: {
      fontSize: Font.md,
      fontWeight: Weight.semibold,
    },
    artist: {
      fontSize: Font.sm,
      color: Colors.textSubtle,
    },
  }), [minWidth]);
}
