/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * A single row in a rankings leaderboard.
 * Displays rank, player name, rating value, and songs played.
 */
import { memo, useMemo } from 'react';
import { Colors, Font, FontVariant, Layout, TextAlign, Weight, truncate } from '@festival/theme';

export interface RankingEntryProps {
  rank: number;
  displayName: string;
  /** Formatted rating value string (e.g. "42.3%", "1,250,000"). */
  ratingLabel: string;
  /** Songs played count (e.g. "142 / 200"). */
  songsLabel?: string;
  isPlayer?: boolean;
  /** Pixel width for the rank column. Computed from the longest rank in the list. */
  rankWidth?: number;
}

export const RankingEntry = memo(function RankingEntry({
  rank,
  displayName,
  ratingLabel,
  songsLabel,
  isPlayer,
  rankWidth,
}: RankingEntryProps) {
  const s = useStyles(isPlayer, rankWidth);

  return (
    <>
      <span style={s.colRank}>#{rank.toLocaleString()}</span>
      <span style={s.colName}>{displayName}</span>
      {songsLabel && <span style={s.colSongs}>{songsLabel}</span>}
      <span style={s.colRating}>{ratingLabel}</span>
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
