import { memo, useMemo, type CSSProperties } from 'react';
import { formatPercentileBucket } from '@festival/core';
import { Gap, Radius, Layout, TextAlign, CssValue, FAST_FADE_MS, transition, padding, frostedCard, flexRow, flexColumn, Display, Align, Justify } from '@festival/theme';
import { CssProp } from '@festival/theme';
import SongInfo from '../../../components/songs/metadata/SongInfo';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';
import { useIsNarrow } from '../../../hooks/ui/useIsMobile';

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
  const isNarrow = useIsNarrow();
  const twoRow = isNarrow && percentile != null;
  const s = useStyles(twoRow);
  return (
    <a key={songId} href={href} onClick={onClick} style={s.songListRow}>
      <div style={twoRow ? s.rowMainLine : { display: Display.contents }}>
        <SongInfo albumArt={albumArt} title={title} artist={artist} year={year} />
        {!twoRow && percentile != null && (
          <div style={s.topSongRight}>
            <PercentilePill display={formatPercentileBucket(percentile)} />
          </div>
        )}
      </div>
      {twoRow && (
        <div style={s.metadataRow}>
          <PercentilePill display={formatPercentileBucket(percentile)} />
        </div>
      )}
    </a>
  );
});

export default PlayerSongRow;

function useStyles(twoRow: boolean) {
  return useMemo(() => ({
    songListRow: {
      ...frostedCard,
      ...(twoRow ? flexColumn : flexRow),
      ...(twoRow ? { alignItems: Align.stretch } : undefined),
      gap: twoRow ? Gap.sm : Gap.xl,
      padding: twoRow ? padding(Gap.md, Gap.xl) : padding(0, Gap.xl),
      ...(twoRow ? { minHeight: Layout.playerSongRowHeight } : { height: Layout.playerSongRowHeight }),
      borderRadius: Radius.md,
      textDecoration: CssValue.none,
      color: CssValue.inherit,
      transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
    } as CSSProperties,
    rowMainLine: {
      ...flexRow,
      gap: Gap.xl,
    } as CSSProperties,
    topSongRight: {
      textAlign: TextAlign.right,
      flexShrink: 0,
    } as CSSProperties,
    metadataRow: {
      display: Display.flex,
      justifyContent: Justify.end,
      gap: Gap.md,
      paddingTop: Gap.sm,
    } as CSSProperties,
  }), [twoRow]);
}
