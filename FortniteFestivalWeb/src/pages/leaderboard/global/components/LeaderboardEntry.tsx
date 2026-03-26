/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * A single row's column content (rank/label, name, season, score, accuracy, stars).
 * Used by LeaderboardPage, InstrumentCard, and PlayerHistoryPage.
 *
 * Provide `rank` for leaderboard rows (#1, #2, …) or `label` for freeform first
 * columns (e.g. date strings in score history). Exactly one should be given.
 */
import { memo, useMemo } from 'react';
import { Align, Colors, Display, Font, FontVariant, Gap, Justify, Layout, TextAlign, Weight, flexRow, truncate } from '@festival/theme';
import SeasonPill from '../../../../components/songs/metadata/SeasonPill';
import ScorePill from '../../../../components/songs/metadata/ScorePill';
import AccuracyDisplay from '../../../../components/songs/metadata/AccuracyDisplay';
import MiniStars from '../../../../components/songs/metadata/MiniStars';

export interface LeaderboardEntryProps {
  /** Numeric rank displayed as "#1", "#2", etc. */
  rank?: number;
  /** Freeform first-column text (e.g. a date). Mutually exclusive with rank. */
  label?: string;
  displayName: string;
  score: number;
  season?: number | null;
  accuracy?: number | null;
  isFullCombo?: boolean;
  stars?: number | null;
  /** Whether this entry should use bold styling (tracked player or high score). */
  isPlayer?: boolean;
  /** Show the season pill column. */
  showSeason?: boolean;
  /** Show the accuracy column. */
  showAccuracy?: boolean;
  /** Show the stars column. */
  showStars?: boolean;
  /** Width string for the score column (e.g. '6ch'). */
  scoreWidth?: string;
}

export const LeaderboardEntry = memo(function LeaderboardEntry({
  rank,
  label,
  displayName,
  score,
  season,
  accuracy,
  isFullCombo,
  stars,
  isPlayer,
  showSeason,
  showAccuracy,
  showStars,
  scoreWidth,
}: LeaderboardEntryProps) {
  const s = useStyles(isPlayer);

  return (
    <>
      {rank != null
        ? <span style={s.colRank}>#{rank.toLocaleString()}</span>
        : null}
      <span style={s.colName}>{label ?? displayName}</span>
      <span style={s.seasonScoreGroup}>
        {showSeason && season != null && <SeasonPill season={season} />}
        <ScorePill score={score} width={scoreWidth} />
      </span>
      {showAccuracy && (
        <span style={s.colAcc}>
          <AccuracyDisplay accuracy={accuracy ?? null} isFullCombo={isFullCombo} />
        </span>
      )}
      {showStars && (
        <span style={s.colStars}>
          {stars != null && stars > 0
            ? <MiniStars starsCount={stars} isFullCombo={!!isFullCombo} />
            : '\u2014'}
        </span>
      )}
    </>
  );
});

function useStyles(isPlayer?: boolean) {
  return useMemo(() => ({
    colRank: {
      width: Layout.rankColumnWidth,
      flexShrink: 0,
      color: Colors.textPrimary,
      fontSize: Font.md,
      ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
    },
    colName: {
      ...truncate,
      flex: 1,
      minWidth: 0,
      ...(isPlayer ? { fontWeight: Weight.bold } : undefined),
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
    colStars: {
      flexShrink: 0,
      display: Display.inlineFlex,
      alignItems: Align.center,
      justifyContent: Justify.end,
    },
  }), [isPlayer]);
}
