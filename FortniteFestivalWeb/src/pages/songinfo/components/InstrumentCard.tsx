import { memo, type CSSProperties } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import AccuracyDisplay from '../../../components/songs/metadata/AccuracyDisplay';
import SeasonPill from '../../../components/songs/metadata/SeasonPill';
import { INSTRUMENT_LABELS, type ServerInstrumentKey as InstrumentKey, type LeaderboardEntry, type PlayerScore } from '@festival/core/api/serverTypes';
import s from './InstrumentCard.module.css';

interface InstrumentCardProps {
  songId: string;
  instrument: InstrumentKey;
  baseDelay: number;
  windowWidth: number;
  playerScore?: PlayerScore;
  playerName?: string;
  playerAccountId?: string;
  prefetchedEntries: LeaderboardEntry[];
  prefetchedError: string | null;
  skipAnimation?: boolean;
  scoreWidth: string;
}

export default memo(function InstrumentCard({
  songId,
  instrument,
  baseDelay,
  windowWidth, playerScore,
  playerName,
  playerAccountId,
  prefetchedEntries,
  prefetchedError,
  skipAnimation,
  scoreWidth,
}: InstrumentCardProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();

  const isTwoCol = windowWidth >= 840;
  const cardWidth = isTwoCol ? windowWidth / 2 : windowWidth;
  const showAccuracy = cardWidth >= 420;
  const showSeason = cardWidth >= 520;
  const isMobile = cardWidth < 360;

  const playerInTop = !!(playerAccountId && prefetchedEntries.some(
    (e) => e.accountId === playerAccountId,
  ));

  const anim = (delayMs: number): CSSProperties => skipAnimation ? {} : ({
    opacity: 0,
    animation: `fadeInUp 300ms ease-out ${delayMs}ms forwards`,
  });
  /* v8 ignore start — animation cleanup */
  const clearAnim = (ev: React.AnimationEvent<HTMLElement>) => {
    ev.currentTarget.style.opacity = '';
    ev.currentTarget.style.animation = '';
  };
  /* v8 ignore stop */

  return (
    <div className={s.cardWrapper}>
      <div className={s.cardLabel} style={anim(baseDelay)} onAnimationEnd={clearAnim}>
        <InstrumentIcon instrument={instrument} size={36} />
        <span className={s.cardTitle}>{INSTRUMENT_LABELS[instrument]}</span>
      </div>
      <div
        className={s.card}
        style={{ cursor: 'pointer' }}
        onClick={() => {
          /* v8 ignore next — navigation */
          navigate(`/songs/${songId}/${instrument}`, { state: { backTo: `/songs/${songId}` } });
        }}
      >
        <div className={s.cardBody}>
        {prefetchedError && <span className={s.cardError}>{prefetchedError}</span>}
        {!prefetchedError && prefetchedEntries.length === 0 && (
          <span className={s.cardMuted}>{t('songDetail.noEntries')}</span>
        )}
        {!prefetchedError &&
          prefetchedEntries.map((e, i) => {
            const rowStagger = anim(baseDelay + 80 + i * 60);
            const isPlayer = playerInTop && e.accountId === playerAccountId;
            const rowClass = `${isPlayer ? s.playerEntryRow : s.entryRow} ${isMobile ? s.entryRowMobile : ''}`;
            return (
            <Link
              key={e.accountId}
              id={isPlayer ? `player-score-${instrument}` : undefined}
              to={`/player/${e.accountId}`}
              state={{ backTo: `/songs/${songId}` }}
              className={rowClass}
              style={rowStagger}
              onClick={(ev) => ev.stopPropagation()}
              onAnimationEnd={clearAnim} /* v8 ignore -- animation cleanup */
            >
              <span className={s.entryRank} style={isPlayer ? { fontWeight: 700 } : undefined}>#{(e.rank ?? i + 1).toLocaleString()}</span>
              <span className={s.entryName} style={isPlayer ? { fontWeight: 700 } : undefined}>
                {e.displayName ?? e.accountId.slice(0, 8)}
              </span>
              <span className={s.seasonScoreGroup}>
                {showSeason && e.season != null && (
                  <SeasonPill season={e.season} />
                )}
                <span className={s.entryScore} style={{ width: scoreWidth }}>
                  {e.score.toLocaleString()}
                </span>
              </span>
              {showAccuracy && (
              <span className={s.entryAcc}>
                <AccuracyDisplay
                  accuracy={e.accuracy}
                  isFullCombo={!!e.isFullCombo}
                />
              </span>
              )}
            </Link>
            );
          })}
        {playerName && playerScore && !playerInTop && (() => {
          const playerDelay = baseDelay + 80 + prefetchedEntries.length * 60;
          const playerStagger = anim(playerDelay);
          return (
          <Link
            id={`player-score-${instrument}`}
            to={`/songs/${songId}/${instrument}?page=${Math.floor((playerScore.rank - 1) / 25) + 1}&navToPlayer=true`}
            className={`${s.playerEntryRow} ${isMobile ? s.entryRowMobile : ''}`}
            style={playerStagger}
            onClick={(ev) => ev.stopPropagation()}
            onAnimationEnd={clearAnim} /* v8 ignore -- animation cleanup */
          >
            <span className={s.entryRank} style={{ fontWeight: 700 }}>#{playerScore.rank.toLocaleString()}</span>
            <span className={s.entryName} style={{ fontWeight: 700 }}>{playerName}</span>
            <span className={s.seasonScoreGroup}>
              {showSeason && playerScore.season != null && (
                <SeasonPill season={playerScore.season} />
              )}
              <span className={s.entryScore} style={{ width: scoreWidth }}>
                {playerScore.score.toLocaleString()}
              </span>
            </span>
            {showAccuracy && (
            <span className={s.entryAcc}>
              <AccuracyDisplay
                accuracy={playerScore.accuracy}
                isFullCombo={!!playerScore.isFullCombo}
              />
            </span>
            )}
          </Link>
          );
        })()}
        {!prefetchedError && prefetchedEntries.length > 0 && (() => {
          const viewAllDelay = baseDelay + 80 + (prefetchedEntries.length + (playerScore && !playerInTop ? 1 : 0)) * 60;
          const viewAllStagger = anim(viewAllDelay);
          return (
            <div
              className={s.viewAllButton} style={viewAllStagger}
              onAnimationEnd={clearAnim}
            >
              View full leaderboard
            </div>
          );
        })()}
      </div>
      </div>
    </div>
  );
});
