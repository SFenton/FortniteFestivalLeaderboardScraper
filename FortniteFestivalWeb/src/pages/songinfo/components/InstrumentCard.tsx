import { memo, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { InstrumentHeaderSize } from '@festival/core';
import InstrumentHeader from '../../../components/display/InstrumentHeader';
import { LeaderboardEntry } from '../../leaderboard/global/components/LeaderboardEntry';
import { computeRankWidth } from '../../leaderboards/helpers/rankingHelpers';
import { leaderboardCache } from '../../../api/pageCache';
import { type ServerInstrumentKey as InstrumentKey, type LeaderboardEntry as LeaderboardEntryType, type PlayerScore } from '@festival/core/api/serverTypes';
import InstrumentEmptyState from '../../player/sections/InstrumentEmptyState';
import { Colors, Font, Weight, Gap, Radius, Layout, Display, Align, Justify, Overflow, Cursor, Opacity, CssValue, FAST_FADE_MS, TRANSITION_MS, STAGGER_ENTRY_OFFSET, STAGGER_ROW_MS, TextAlign, WhiteSpace, WordBreak, frostedCard, flexColumn, flexRow, transition, padding, border, Border } from '@festival/theme';
import { CssProp } from '@festival/theme';
import { parseApiError } from '../../../utils/apiError';
import { useContainerWidth } from '../../../hooks/ui/useContainerWidth';
import { resolveTopScoresColumns } from '../topScoresLayout';

interface InstrumentCardProps {
  songId: string;
  instrument: InstrumentKey;
  baseDelay: number;
  windowWidth: number;
  singleColumn?: boolean;
  playerScore?: PlayerScore;
  playerName?: string;
  playerAccountId?: string;
  prefetchedEntries: LeaderboardEntryType[];
  prefetchedError: string | null;
  /** Total entries reported by Epic for this instrument's leaderboard (if known). */
  totalEntries?: number;
  /** Entries tracked locally by FST for this instrument's leaderboard (if known). */
  localEntries?: number;
  skipAnimation?: boolean;
  scoreWidth: string;
  sig?: string;
}

export default memo(function InstrumentCard({
  songId,
  instrument,
  baseDelay,
  windowWidth,
  singleColumn,
  playerScore,
  playerName,
  playerAccountId,
  prefetchedEntries,
  prefetchedError,
  totalEntries,
  localEntries,
  skipAnimation,
  scoreWidth,
  sig,
}: InstrumentCardProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();

  const isTwoCol = windowWidth >= 840 && !singleColumn;
  const cardRef = useRef<HTMLDivElement | null>(null);
  const measuredCardWidth = useContainerWidth(cardRef);
  const estimatedCardWidth = isTwoCol ? windowWidth / 2 : windowWidth;
  const cardWidth = measuredCardWidth > 0 ? measuredCardWidth : estimatedCardWidth;
  const { isCompactCard, showAccuracy, showSeason } = resolveTopScoresColumns(cardWidth);
  const viewAllButtonRef = useRef<HTMLDivElement | null>(null);
  const [viewAllNeedsCompact, setViewAllNeedsCompact] = useState(false);
  const effectiveScoreWidth = isCompactCard ? undefined : scoreWidth;

  const playerInTop = !!(playerAccountId && prefetchedEntries.some(
    (e) => e.accountId === playerAccountId,
  ));
  const hasEntries = prefetchedEntries.length > 0 || (!!playerScore && !playerInTop);

  const rankWidth = useMemo(() => {
    const ranks = prefetchedEntries.map((e, i) => e.rank ?? i + 1);
    if (playerScore && !playerInTop) ranks.push(playerScore.rank);
    return computeRankWidth(ranks);
  }, [prefetchedEntries, playerScore, playerInTop]);

  const hasViewAllCounts = totalEntries != null && localEntries != null && totalEntries > 0;
  const fullViewAllLabel = hasViewAllCounts
    ? t('leaderboard.viewFullWithCounts', {
        local: localEntries!.toLocaleString(),
        total: totalEntries!.toLocaleString(),
      })
    : t('leaderboard.viewPlain');

  useLayoutEffect(() => {
    if (!hasViewAllCounts) {
      setViewAllNeedsCompact(false);
      return;
    }

    const button = viewAllButtonRef.current;
    if (!button) return;

    const measure = () => {
      const computed = getComputedStyle(button);
      const paddingLeft = Number.parseFloat(computed.paddingLeft) || 0;
      const paddingRight = Number.parseFloat(computed.paddingRight) || 0;
      const availableWidth = button.clientWidth - paddingLeft - paddingRight;
      if (availableWidth <= 0) return;

      const canvas = document.createElement('canvas');
      const context = canvas.getContext('2d');
      if (!context) return;

      context.font = [computed.fontStyle, computed.fontVariant, computed.fontWeight, computed.fontSize, computed.fontFamily]
        .filter((part) => !!part && part !== 'normal')
        .join(' ');
      const next = context.measureText(fullViewAllLabel).width > availableWidth;
      setViewAllNeedsCompact((current) => current === next ? current : next);
    };

    measure();
    if (typeof ResizeObserver === 'undefined') return;

    const resizeObserver = new ResizeObserver(measure);
    resizeObserver.observe(button);
    return () => resizeObserver.disconnect();
  }, [cardWidth, fullViewAllLabel, hasViewAllCounts]);

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

  const st = useInstrumentCardStyles();

  return (
    <div style={st.cardWrapper}>
      <div style={{ ...st.cardLabel, ...anim(baseDelay) }} onAnimationEnd={clearAnim}>
        <InstrumentHeader instrument={instrument} size={InstrumentHeaderSize.MD} sig={sig} />
      </div>
      <div
        ref={cardRef}
        style={hasEntries ? st.card : st.cardNoClick}
        /* v8 ignore start — navigation */
        {...(hasEntries ? { onClick: () => { leaderboardCache.delete(`${songId}:${instrument}`); navigate(`/songs/${songId}/${instrument}`, { state: { backTo: `/songs/${songId}` } }); } } : {})}
        /* v8 ignore stop */
      >
        <div style={st.cardBody}>
        {prefetchedError && <span style={st.cardError}>{parseApiError(prefetchedError).title}</span>}
        {!prefetchedError && prefetchedEntries.length === 0 && !playerScore && (
          <div style={{ ...anim(baseDelay + STAGGER_ENTRY_OFFSET) }} onAnimationEnd={clearAnim}>
            <InstrumentEmptyState instrument={instrument} t={t} noMargin subtitleKey="songDetail.noScoresSubtitle" />
          </div>
        )}
        {!prefetchedError &&
          prefetchedEntries.map((e, i) => {
            const rowStagger = anim(baseDelay + STAGGER_ENTRY_OFFSET + i * STAGGER_ROW_MS);
            const isPlayer = playerInTop && e.accountId === playerAccountId;
            const rowStyle = { ...(isPlayer ? st.playerEntryRow : st.entryRow), ...(isCompactCard ? st.entryRowMobile : {}) };
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
                difficulty={e.difficulty}
                showDifficulty={showSeason}
                showSeason={showSeason}
                showAccuracy={showAccuracy}
                scoreWidth={effectiveScoreWidth}
                rankWidth={rankWidth}
              />
            </Link>
            );
          })}
        {/* v8 ignore start — player score IIFE; conditionally rendered animation block */}
        {playerName && playerScore && !playerInTop && (() => {
          const playerDelay = baseDelay + STAGGER_ENTRY_OFFSET + prefetchedEntries.length * STAGGER_ROW_MS;
          const playerStagger = anim(playerDelay);
          const playerRowStyle = { ...st.playerEntryRow, ...(isCompactCard ? st.entryRowMobile : {}) };
          return (
          <Link
            id={`player-score-${instrument}`}
            to={`/songs/${songId}/${instrument}?page=${Math.floor(((playerScore.localRank ?? playerScore.rank) - 1) / 25) + 1}&navToPlayer=true`}
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
              difficulty={playerScore.difficulty}
              showDifficulty={showSeason}
              showSeason={showSeason}
              showAccuracy={showAccuracy}
              scoreWidth={effectiveScoreWidth}
              rankWidth={rankWidth}
            />
          </Link>
          );
        })()}
        {/* v8 ignore stop */}
        {/* v8 ignore start — view all IIFE; conditionally rendered animation block */}
        {!prefetchedError && prefetchedEntries.length > 0 && (() => {
          const viewAllDelay = baseDelay + STAGGER_ENTRY_OFFSET + (prefetchedEntries.length + (playerScore && !playerInTop ? 1 : 0)) * STAGGER_ROW_MS;
          const viewAllStagger = anim(viewAllDelay);
          const shouldUseCompactViewAll = hasViewAllCounts && (isCompactCard || viewAllNeedsCompact);
          if (shouldUseCompactViewAll) {
            return (
              <div
                ref={viewAllButtonRef}
                style={{ ...st.viewAllButtonCompact, ...viewAllStagger }}
                onAnimationEnd={clearAnim}
              >
                <span>{t('leaderboard.viewFullShort')}</span>
                <span>{t('leaderboard.trackedCount', { count: localEntries!.toLocaleString() as unknown as number })}</span>
                <span>{t('leaderboard.totalCount', { count: totalEntries!.toLocaleString() as unknown as number })}</span>
              </div>
            );
          }
          return (
            <div
              ref={viewAllButtonRef}
              style={{ ...st.viewAllButton, ...viewAllStagger }}
              onAnimationEnd={clearAnim}
            >
              {fullViewAllLabel}
            </div>
          );
        })()}
        {/* v8 ignore stop */}
      </div>
      </div>
    </div>
  );
});

function useInstrumentCardStyles() {
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
      cardNoClick: {
        ...flexColumn,
        height: '100%',
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
        position: 'relative',
        alignItems: Align.center,
        justifyContent: Justify.center,
        minHeight: Layout.entryRowHeight,
        padding: padding(Gap.sm, Gap.md),
        borderRadius: Radius.md,
        color: Colors.textPrimary,
        fontSize: Font.md,
        fontWeight: Weight.semibold,
        cursor: Cursor.pointer,
        textAlign: TextAlign.center,
        whiteSpace: WhiteSpace.nowrap,
        wordBreak: WordBreak.normal,
        transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      } as CSSProperties,
      viewAllButtonCompact: {
        ...frostedCard,
        display: Display.flex,
        position: 'relative',
        flexDirection: 'column',
        alignItems: Align.center,
        justifyContent: Justify.center,
        minHeight: Layout.entryRowHeight,
        gap: Gap.xs,
        padding: padding(Gap.sm, Gap.md),
        borderRadius: Radius.md,
        color: Colors.textPrimary,
        fontSize: Font.md,
        fontWeight: Weight.semibold,
        cursor: Cursor.pointer,
        textAlign: TextAlign.center,
        whiteSpace: WhiteSpace.nowrap,
        wordBreak: WordBreak.normal,
        transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      } as CSSProperties,
    };
  }, []);
}
