/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * A single row in the player score history list.
 * Extracted for testability and reuse.
 */
import { memo, useMemo } from 'react';
import { Colors, Font, FontVariant, Gap, Layout, TextAlign, Weight, flexRow, truncate } from '@festival/theme';
import SeasonPill from '../../../../components/songs/metadata/SeasonPill';
import ScorePill from '../../../../components/songs/metadata/ScorePill';
import AccuracyDisplay from '../../../../components/songs/metadata/AccuracyDisplay';

export interface PlayerHistoryEntryProps {
  /** Date string to display (pre-formatted). */
  date: string;
  /** The score achieved. */
  score: number;
  /** Season number, if available. */
  season?: number | null;
  /** Raw accuracy value (0null if unavailable. */
  accuracy?: number | null;
  /** Whether this entry was a full combo. */
  isFullCombo?: boolean;
  /** Whether this is the highest score in the list. */
  isHighScore?: boolean;
  /** Show the season pill column. */
  showSeason?: boolean;
  /** Show the accuracy column. */
  showAccuracy?: boolean;
  /** Width string for the score column (e.g. '6ch'). */
  scoreWidth?: string;
}

export const PlayerHistoryEntry = memo(function PlayerHistoryEntry({
  date,
  score,
  season,
  accuracy,
  isFullCombo,
  isHighScore,
  showSeason,
  showAccuracy,
  scoreWidth,
}: PlayerHistoryEntryProps) {
  const s = useStyles(isHighScore);

  return (
    <>
      <span style={s.colName}>{date}</span>
      <span style={s.seasonScoreGroup}>
        {showSeason && season != null && (
          <SeasonPill season={season} />
        )}
        <ScorePill score={score} width={scoreWidth} />
      </span>
      {showAccuracy && (
        <span style={s.colAcc}>
          <AccuracyDisplay accuracy={accuracy ?? null} isFullCombo={isFullCombo} />
        </span>
      )}
    </>
  );
});

function useStyles(isHighScore?: boolean) {
  return useMemo(() => ({
    colName: {
      ...truncate,
      flex: 1,
      minWidth: 0,
      ...(isHighScore ? { fontWeight: Weight.bold } : undefined),
    },
    seasonScoreGroup: {
      ...flexRow,
      gap: Gap.md,
      flexShrink: 0,
    },
    colAcc: {
      width: Layout.accColumnWidth,
      flexShrink: 0,
      textAlign: TextAlign.center,
      fontWeight: Weight.semibold,
      fontSize: Font.lg,
      color: Colors.accentBlueBright,
      fontVariantNumeric: FontVariant.tabularNums,
    },
  }), [isHighScore]);
}
