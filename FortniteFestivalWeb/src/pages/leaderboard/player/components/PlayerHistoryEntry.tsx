/**
 * A single row in the player score history list.
 * Extracted for testability and reuse.
 */
import { memo } from 'react';
import SeasonPill from '../../../../components/songs/metadata/SeasonPill';
import AccuracyDisplay from '../../../../components/songs/metadata/AccuracyDisplay';
import shared from '../../../../styles/shared.module.css';
import s from './PlayerHistoryEntry.module.css';

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

  return (
    <>
      <span className={`${s.colName}${isHighScore ? ` ${shared.textBold}` : ''}`}>{date}</span>
      <span className={s.seasonScoreGroup}>
        {showSeason && season != null && (
          <SeasonPill season={season} />
        )}
        <span className={s.colScore} style={scoreWidth ? { width: scoreWidth } : undefined}>
          {score.toLocaleString()}
        </span>
      </span>
      {showAccuracy && (
        <span className={s.colAcc}>
          <AccuracyDisplay accuracy={accuracy ?? null} isFullCombo={isFullCombo} />
        </span>
      )}
    </>
  );
});
