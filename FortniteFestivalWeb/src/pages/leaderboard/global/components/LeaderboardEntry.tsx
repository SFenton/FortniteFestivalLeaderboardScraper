/**
 * A single leaderboard row's column content (rank, name, season, score, accuracy, stars).
 * Used by both the scrollable list rows and the sticky player footer in LeaderboardPage.
 */
import { memo } from 'react';
import SeasonPill from '../../../../components/songs/metadata/SeasonPill';
import AccuracyDisplay from '../../../../components/songs/metadata/AccuracyDisplay';
import shared from '../../../../styles/shared.module.css';
import s from './LeaderboardEntry.module.css';

export interface LeaderboardEntryProps {
  rank: number;
  displayName: string;
  score: number;
  season?: number | null;
  accuracy?: number | null;
  isFullCombo?: boolean;
  stars?: number | null;
  /** Whether this entry belongs to the tracked player (bold styling). */
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
      <span className={`${s.colRank}${bold}`}>#{rank.toLocaleString()}</span>
      <span className={`${s.colName}${bold}`}>{displayName}</span>
      <span className={s.seasonScoreGroup}>
        {showSeason && season != null && <SeasonPill season={season} />}
        <span className={s.colScore} style={scoreWidth ? { width: scoreWidth } : undefined}>
          {score.toLocaleString()}
        </span>
      </span>
      {showAccuracy && (
        <span className={s.colAcc}>
          <AccuracyDisplay accuracy={accuracy ?? null} isFullCombo={isFullCombo} />
        </span>
      )}
      {showStars && (
        <span className={s.colStars}>
          {/* v8 ignore start — star rendering ternary */}
          {stars != null && stars > 0
            ? (() => {
                const allGold = stars >= 6;
                const count = allGold ? 5 : stars;
                const src = allGold
                  ? `${import.meta.env.BASE_URL}star_gold.png`
                  : `${import.meta.env.BASE_URL}star_white.png`;
                return Array.from({ length: count }, (_, i) => (
                  <img key={i} src={src} alt="Ã¢Ëœâ€¦" className={s.starImg} />
                ));
              })()
            : '\u2014'}
        </span>
      )}
    </>
  );
});
