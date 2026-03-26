import { memo, useMemo, type CSSProperties } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { InstrumentHeaderSize } from '@festival/core';
import InstrumentHeader from '../../../components/display/InstrumentHeader';
import { LeaderboardEntry } from '../../leaderboard/global/components/LeaderboardEntry';
import { type ServerInstrumentKey as InstrumentKey, type LeaderboardEntry as LeaderboardEntryType, type PlayerScore } from '@festival/core/api/serverTypes';
import { QUERY_SHOW_ACCURACY, QUERY_SHOW_SEASON, Colors, Font, Weight, Gap, Radius, Layout, Display, Align, Justify, Overflow, Cursor, Opacity, CssValue, FAST_FADE_MS, TRANSITION_MS, STAGGER_ENTRY_OFFSET, STAGGER_ROW_MS, frostedCard, flexColumn, flexRow, transition, padding, border, Border } from '@festival/theme';
import { CssProp } from '@festival/theme';
import { useMediaQuery } from '../../../hooks/ui/useMediaQuery';

interface InstrumentCardProps {
  songId: string;
  instrument: InstrumentKey;
  baseDelay: number;
  windowWidth: number;
  playerScore?: PlayerScore;
  playerName?: string;
  playerAccountId?: string;
  prefetchedEntries: LeaderboardEntryType[];
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
  const showAccuracy = useMediaQuery(QUERY_SHOW_ACCURACY);
  const showSeason = useMediaQuery(QUERY_SHOW_SEASON);
  const isMobile = cardWidth < 360;

  const playerInTop = !!(playerAccountId && prefetchedEntries.some(
    (e) => e.accountId === playerAccountId,
  ));

  const anim = (delayMs: number): CSSProperties => skipAnimation ? {} : ({
    opacity: Opacity.none,
    animation: `fadeInUp ${TRANSITION_MS}ms ease-out ${delayMs}ms forwards`,
  });
  /* v8 ignore start — animation cleanup */
  const clearAnim = (ev: React.AnimationEvent<HTMLElement>) => {
    ev.currentTarget.style.opacity = '';
    ev.currentTarget.style.animation = '';
  };
  /* v8 ignore stop */

  const st = useInstrumentCardStyles(isMobile);

  return (
    <div style={st.cardWrapper}>
      <div style={{ ...st.cardLabel, ...anim(baseDelay) }} onAnimationEnd={clearAnim}>
        <InstrumentHeader instrument={instrument} size={InstrumentHeaderSize.MD} />
      </div>
      <div
        style={st.card}
        /* v8 ignore start — navigation */
        onClick={() => {
          navigate(`/songs/${songId}/${instrument}`, { state: { backTo: `/songs/${songId}` } });
        }}
        /* v8 ignore stop */
      >
        <div style={st.cardBody}>
        {prefetchedError && <span style={st.cardError}>{prefetchedError}</span>}
        {!prefetchedError && prefetchedEntries.length === 0 && (
          <span style={st.cardMuted}>{t('songDetail.noEntries')}</span>
        )}
        {!prefetchedError &&
          prefetchedEntries.map((e, i) => {
            const rowStagger = anim(baseDelay + STAGGER_ENTRY_OFFSET + i * STAGGER_ROW_MS);
            const isPlayer = playerInTop && e.accountId === playerAccountId;
            const rowStyle = { ...(isPlayer ? st.playerEntryRow : st.entryRow), ...(isMobile ? st.entryRowMobile : {}) };
            return (
            <Link
              key={e.accountId}
              id={isPlayer ? `player-score-${instrument}` : undefined}
              to={`/player/${e.accountId}`}
              state={{ backTo: `/songs/${songId}` }}
              style={{ ...rowStyle, ...rowStagger }}
              onClick={(ev) => ev.stopPropagation()}
              onAnimationEnd={clearAnim} /* v8 ignore -- animation cleanup */
            >
              <LeaderboardEntry
                rank={e.rank ?? i + 1}
                displayName={e.displayName ?? e.accountId.slice(0, 8)}
                score={e.score}
                season={e.season}
                accuracy={e.accuracy}
                isFullCombo={!!e.isFullCombo}
                isPlayer={isPlayer}
                showSeason={showSeason}
                showAccuracy={showAccuracy}
                scoreWidth={scoreWidth}
              />
            </Link>
            );
          })}
        {/* v8 ignore start — player score IIFE; conditionally rendered animation block */}
        {playerName && playerScore && !playerInTop && (() => {
          const playerDelay = baseDelay + STAGGER_ENTRY_OFFSET + prefetchedEntries.length * STAGGER_ROW_MS;
          const playerStagger = anim(playerDelay);
          const playerRowStyle = { ...st.playerEntryRow, ...(isMobile ? st.entryRowMobile : {}) };
          return (
          <Link
            id={`player-score-${instrument}`}
            to={`/songs/${songId}/${instrument}?page=${Math.floor((playerScore.rank - 1) / 25) + 1}&navToPlayer=true`}
            style={{ ...playerRowStyle, ...playerStagger }}
            onClick={(ev) => ev.stopPropagation()}
            onAnimationEnd={clearAnim} /* v8 ignore -- animation cleanup */
          >
            <LeaderboardEntry
              rank={playerScore.rank}
              displayName={playerName}
              score={playerScore.score}
              season={playerScore.season}
              accuracy={playerScore.accuracy}
              isFullCombo={!!playerScore.isFullCombo}
              isPlayer
              showSeason={showSeason}
              showAccuracy={showAccuracy}
              scoreWidth={scoreWidth}
            />
          </Link>
          );
        })()}
        {/* v8 ignore stop */}
        {/* v8 ignore start — view all IIFE; conditionally rendered animation block */}
        {!prefetchedError && prefetchedEntries.length > 0 && (() => {
          const viewAllDelay = baseDelay + STAGGER_ENTRY_OFFSET + (prefetchedEntries.length + (playerScore && !playerInTop ? 1 : 0)) * STAGGER_ROW_MS;
          const viewAllStagger = anim(viewAllDelay);
          return (
            <div
              style={{ ...st.viewAllButton, ...viewAllStagger }}
              onAnimationEnd={clearAnim}
            >
              {t('leaderboard.viewFull')}
            </div>
          );
        })()}
        {/* v8 ignore stop */}
      </div>
      </div>
    </div>
  );
});

function useInstrumentCardStyles(_isMobile: boolean) {
  return useMemo(() => {
    const entryBase: CSSProperties = {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(0, Gap.xl),
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      textDecoration: CssValue.none,
      color: CssValue.inherit,
      transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      fontSize: Font.md,
    };
    return {
      cardWrapper: { ...flexColumn } as CSSProperties,
      cardLabel: {
        ...flexRow,
        gap: Gap.md,
        paddingBottom: Gap.xs,
      } as CSSProperties,
      card: {
        ...flexColumn,
        height: '100%',
        cursor: Cursor.pointer,
      } as CSSProperties,
      cardBody: {
        ...flexColumn,
        gap: Gap.sm,
        flex: 1,
        overflow: Overflow.hidden,
      } as CSSProperties,
      cardMuted: {
        fontSize: Font.sm,
        color: Colors.textMuted,
      } as CSSProperties,
      cardError: {
        fontSize: Font.sm,
        color: Colors.statusRed,
      } as CSSProperties,
      entryRow: { ...entryBase } as CSSProperties,
      entryRowMobile: {
        gap: Gap.md,
        padding: padding(0, Gap.md),
      } as CSSProperties,
      playerEntryRow: {
        ...entryBase,
        backgroundColor: Colors.purpleHighlight,
        border: border(Border.thin, Colors.purpleHighlightBorder),
      } as CSSProperties,
      viewAllButton: {
        ...frostedCard,
        display: Display.flex,
        alignItems: Align.center,
        justifyContent: Justify.center,
        height: Layout.entryRowHeight,
        borderRadius: Radius.md,
        color: Colors.textPrimary,
        fontSize: Font.md,
        fontWeight: Weight.semibold,
        cursor: Cursor.pointer,
        transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      } as CSSProperties,
    };
  }, []);
}
