/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * Shared song info block: album art thumbnail + title + artist · year.
 * Used across song rows, player song rows, and suggestion cards.
 */
import { memo, useMemo } from 'react';
import { Colors, Font, Gap, Size, Weight, flexColumn } from '@festival/theme';
import AlbumArt from './AlbumArt';
import MarqueeText from '../../common/MarqueeText';
import { useMarqueeSync } from '../../../hooks/ui/useMarqueeSync';
import { formatDuration } from '../../../utils/formatters';

export interface SongInfoProps {
  albumArt?: string;
  title: string;
  artist: string;
  year?: number;
  durationSeconds?: number;
  minWidth?: number;
}

const SongInfo = memo(function SongInfo({ albumArt, title, artist, year, durationSeconds, minWidth }: SongInfoProps) {
  const s = useStyles(minWidth);
  const duration = formatDuration(durationSeconds);
  const artistText = `${artist}${year ? ` \u00b7 ${year}` : ''}${duration ? ` \u00b7 ${duration}` : ''}`;
  const { reporters, syncDistance } = useMarqueeSync(2);
  return (
    <>
      <AlbumArt src={albumArt} size={Size.thumb} />
      <div style={s.text}>
        <MarqueeText text={title} as="p" style={s.title} onMeasure={reporters[0]} syncDistance={syncDistance} />
        <MarqueeText text={artistText} as="p" style={s.artist} onMeasure={reporters[1]} syncDistance={syncDistance} />
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
