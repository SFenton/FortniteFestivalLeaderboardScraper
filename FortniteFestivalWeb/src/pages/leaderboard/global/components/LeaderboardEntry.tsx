/**
 * A single row's column content (rank/label, name, season, score, accuracy, stars).
 * Used by LeaderboardPage, InstrumentCard, and PlayerHistoryPage.
 *
 * Provide `rank` for leaderboard rows (#1, #2, …) or `label` for freeform first
 * columns (e.g. date strings in score history). Exactly one should be given.
 */
import { memo } from 'react';
import SeasonPill from '../../../../components/songs/metadata/SeasonPill';
import ScorePill from '../../../../components/songs/metadata/ScorePill';
import AccuracyDisplay from '../../../../components/songs/metadata/AccuracyDisplay';
import MiniStars from '../../../../components/songs/metadata/MiniStars';
import shared from '../../../../styles/shared.module.css';
import s from './LeaderboardEntry.module.css';

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
  const bold = isPlayer ? ` ${shared.textBold}` : '';

  return (
    <>
      {rank != null
        ? <span className={`${s.colRank}${bold}`}>#{rank.toLocaleString()}</span>
        : null}
      <span className={`${s.colName}${bold}`}>{label ?? displayName}</span>
      <span className={s.seasonScoreGroup}>
        {showSeason && season != null && <SeasonPill season={season} />}
        <ScorePill score={score} width={scoreWidth} />
      </span>
      {showAccuracy && (
        <span className={s.colAcc}>
          <AccuracyDisplay accuracy={accuracy ?? null} isFullCombo={isFullCombo} />
        </span>
      )}
      {showStars && (
        <span className={s.colStars}>
          {stars != null && stars > 0
            ? <MiniStars starsCount={stars} isFullCombo={!!isFullCombo} />
            : '\u2014'}
        </span>
      )}
    </>
  );
});
