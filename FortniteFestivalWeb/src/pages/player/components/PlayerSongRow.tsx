import { memo, useMemo, type CSSProperties } from 'react';
import { formatPercentileBucket } from '@festival/core';
import { Gap, Radius, Layout, TextAlign, CssValue, FAST_FADE_MS, transition, padding, frostedCard, flexRow } from '@festival/theme';
import { CssProp } from '@festival/theme';
import SongInfo from '../../../components/songs/metadata/SongInfo';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';

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
  const s = useStyles();
  return (
    <a key={songId} href={href} onClick={onClick} style={s.songListRow}>
      <SongInfo albumArt={albumArt} title={title} artist={artist} year={year} />
      <div style={s.topSongRight}>
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
      padding: padding(0, Gap.xl),
      height: Layout.playerSongRowHeight,
      borderRadius: Radius.md,
      textDecoration: CssValue.none,
      color: CssValue.inherit,
      transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
    } as CSSProperties,
    topSongRight: {
      textAlign: TextAlign.right,
      flexShrink: 0,
    } as CSSProperties,
  }), []);
}
