/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * Demo wrapper that provides real songs from useDemoSongs to MetricInfoSlide.
 * Used by "How {Metric} Works" slides to show song rows with album art.
 */
import { useMemo } from 'react';
import MetricInfoSlide, { type MetricInfoSlideProps, type SongExampleRow } from './MetricInfoSlide';
import { useDemoSongs } from '../../../../hooks/data/useDemoSongs';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { Size, Gap } from '@festival/theme';

const ROW_HEIGHT = Size.thumb + Gap.sm * 2;

export interface SongDemoSlideProps extends Omit<MetricInfoSlideProps, 'songRows'> {
  /** Transforms each demo song into a song example row with a computed value label. */
  buildRows: (songs: { albumArt?: string; title: string; artist: string }[]) => SongExampleRow[];
  /** Max songs to show (default: 3). */
  maxSongs?: number;
}

export default function SongDemoSlide({ buildRows, maxSongs = 3, ...rest }: SongDemoSlideProps) {
  const isMobile = useIsMobile();
  const { pool } = useDemoSongs({ rowHeight: ROW_HEIGHT, mobileRowHeight: ROW_HEIGHT, isMobile, autoSwap: false });

  const songRows = useMemo(
    () => buildRows(pool.slice(0, maxSongs)),
    [pool, maxSongs, buildRows],
  );

  return <MetricInfoSlide {...rest} songRows={songRows} />;
}
