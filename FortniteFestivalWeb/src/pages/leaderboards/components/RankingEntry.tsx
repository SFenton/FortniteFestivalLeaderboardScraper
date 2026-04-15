/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * A single row in a rankings leaderboard.
 * Displays rank, player name, rating value, and songs played.
 */
import { memo, useMemo } from 'react';
import { Colors, Font, FontVariant, Layout, MetadataSize, TextAlign, Weight, truncate } from '@festival/theme';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';

export interface RankingEntryProps {
  rank: number;
  displayName: string;
  /** Formatted rating value string (e.g. "42.3%", "1,250,000"). */
  ratingLabel: string;
  /** Songs played count (e.g. "142 / 200"). */
  songsLabel?: string;
  /** Percentile display string (e.g. "Top 0.05%"). When present, renders a PercentilePill instead of songsLabel. */
  percentileDisplay?: string;
  /** Formatted rating value for a value pill (e.g. "0.012"). Shown to the left of the percentile pill. */
  valueDisplay?: string;
  /** Background color for the value pill (e.g. from rankColor()). */
  valueColor?: string;
  /** When set, renders ratingLabel as a PercentilePill with the given tier instead of plain text. */
  ratingPillTier?: 'top1' | 'top5' | 'default';
  /** When true, songs label uses primary (white) text at standard font size. */
  songsLabelPrimary?: boolean;
  isPlayer?: boolean;
  /** Pixel width for the rank column. Computed from the longest rank in the list. */
  rankWidth?: number;
}

export const RankingEntry = memo(function RankingEntry({
  rank,
  displayName,
  ratingLabel,
  songsLabel,
  percentileDisplay,
  valueDisplay,
  valueColor,
  ratingPillTier,
  songsLabelPrimary,
  isPlayer,
  rankWidth,
}: RankingEntryProps) {
  const s = useStyles(isPlayer, rankWidth);

  return (
    <>
      <span style={s.colRank}>#{rank.toLocaleString()}</span>
      <span style={s.colName}>{displayName}</span>
      {valueDisplay && <PercentilePill display={valueDisplay} color={valueColor} minWidth={MetadataSize.valuePillMinWidth} />}
      {!valueDisplay && percentileDisplay ? <PercentilePill display={percentileDisplay} /> : !valueDisplay && songsLabel && <span style={songsLabelPrimary ? s.colSongsPrimary : s.colSongs}>{songsLabel}</span>}
      {ratingPillTier ? <PercentilePill display={ratingLabel} tier={ratingPillTier} /> : ratingLabel && <span style={s.colRating}>{ratingLabel}</span>}
    </>
  );
});

function useStyles(isPlayer?: boolean, rankWidth?: number) {
  return useMemo(() => ({
    colRank: {
      width: rankWidth ?? Layout.rankColumnWidth,
      flexShrink: 0,
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontVariantNumeric: FontVariant.tabularNums,
      ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
    },
    colName: {
      ...truncate,
      flex: 1,
      minWidth: 0,
      ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
    },
    colSongs: {
      flexShrink: 0,
      fontSize: Font.sm,
      color: Colors.textSecondary,
      fontVariantNumeric: FontVariant.tabularNums,
      textAlign: TextAlign.right,
    },
    colSongsPrimary: {
      flexShrink: 0,
      fontSize: Font.md,
      color: Colors.textPrimary,
      fontVariantNumeric: FontVariant.tabularNums,
      textAlign: TextAlign.right,
    },
    colRating: {
      flexShrink: 0,
      fontWeight: Weight.semibold,
      fontSize: Font.md,
      color: Colors.accentBlueBright,
      fontVariantNumeric: FontVariant.tabularNums,
      textAlign: TextAlign.right,
      minWidth: '5ch',
    },
  }), [isPlayer, rankWidth]);
}
