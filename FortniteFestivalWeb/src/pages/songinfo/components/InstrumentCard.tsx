import { memo, useCallback, useLayoutEffect, useMemo, useRef, useState, type AnimationEventHandler, type CSSProperties, type MouseEventHandler, type ReactNode } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { InstrumentHeaderSize } from '@festival/core';
import InstrumentHeader from '../../../components/display/InstrumentHeader';
import { LeaderboardEntry } from '../../leaderboard/global/components/LeaderboardEntry';
import { computeRankWidth } from '../../leaderboards/helpers/rankingHelpers';
import { leaderboardCache } from '../../../api/pageCache';
import { type ServerInstrumentKey as InstrumentKey, type LeaderboardEntry as LeaderboardEntryType, type PlayerScore, type SelectedMemberSongScore } from '@festival/core/api/serverTypes';
import InstrumentEmptyState from '../../player/sections/InstrumentEmptyState';
import { Colors, Font, Gap, Radius, Layout, Display, Align, Overflow, Cursor, Opacity, CssValue, FAST_FADE_MS, TRANSITION_MS, STAGGER_ENTRY_OFFSET, STAGGER_ROW_MS, frostedCard, flexColumn, flexRow, transition, padding, border, Border } from '@festival/theme';
import { CssProp } from '@festival/theme';
import { parseApiError } from '../../../utils/apiError';
import { useContainerWidth } from '../../../hooks/ui/useContainerWidth';
import { useNavLinkPress } from '../../../hooks/navigation/useNavLinkPress';
import { useCardPressAction } from '../../../hooks/ui/usePressAction';
import { resolveTopScoresColumns } from '../topScoresLayout';
import CollapsePresence from '../../../components/common/CollapsePresence';
import ViewFullLeaderboardCta from './ViewFullLeaderboardCta';

interface InstrumentCardProps {
  songId: string;
  instrument: InstrumentKey;
  baseDelay: number;
  windowWidth: number;
  singleColumn?: boolean;
  playerScore?: PlayerScore;
  playerName?: string;
  playerAccountId?: string;
  spotlightScores?: SelectedMemberSongScore[];
  prefetchedEntries: LeaderboardEntryType[];
  prefetchedError: string | null;
  /** Total entries reported by Epic for this instrument's leaderboard (if known). */
  totalEntries?: number;
  /** Entries tracked locally by FST for this instrument's leaderboard (if known). */
  localEntries?: number;
  showLeaderboardEntryTotals?: boolean;
  skipAnimation?: boolean;
  scoreWidth: string;
  sig?: string;
}

type InstrumentSpotlightScore = PlayerScore & {
  accountId: string;
  displayName: string;
};

function normalizeAccountId(accountId: string | null | undefined): string {
  return accountId?.trim().toLowerCase() ?? '';
}

function getSpotlightRowId(instrument: InstrumentKey, accountId: string, playerAccountId: string | undefined): string {
  return normalizeAccountId(accountId) === normalizeAccountId(playerAccountId)
    ? `player-score-${instrument}`
    : `member-score-${instrument}-${accountId}`;
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
  spotlightScores,
  prefetchedEntries,
  prefetchedError,
  totalEntries,
  localEntries,
  showLeaderboardEntryTotals,
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
  const showInlineAccuracyAfterScore = isCompactCard && !isTwoCol;

  const topAccountIds = useMemo(
    () => new Set(prefetchedEntries.map(entry => normalizeAccountId(entry.accountId))),
    [prefetchedEntries],
  );
  const spotlightRows = useMemo(() => {
    const rows: InstrumentSpotlightScore[] = [];
    const seen = new Set<string>();
    const addRow = (score: PlayerScore, accountId: string, displayName: string | null | undefined) => {
      const normalizedAccountId = normalizeAccountId(accountId);
      if (!normalizedAccountId || seen.has(normalizedAccountId)) return;
      seen.add(normalizedAccountId);
      rows.push({
        ...score,
        accountId,
        displayName: displayName || accountId.slice(0, 8),
      });
    };

    if (playerScore && playerName && playerAccountId) {
      addRow(playerScore, playerAccountId, playerName);
    }
    for (const score of spotlightScores ?? []) {
      addRow(score, score.accountId, score.displayName);
    }
    return rows;
  }, [playerAccountId, playerName, playerScore, spotlightScores]);
  const spotlightAccountIds = useMemo(
    () => {
      const accountIds = new Set(spotlightRows.map(score => normalizeAccountId(score.accountId)));
      if (playerAccountId) accountIds.add(normalizeAccountId(playerAccountId));
      return accountIds;
    },
    [playerAccountId, spotlightRows],
  );
  const spotlightFooterRows = useMemo(
    () => spotlightRows
      .filter(score => !topAccountIds.has(normalizeAccountId(score.accountId)))
      .sort((a, b) => a.rank - b.rank),
    [spotlightRows, topAccountIds],
  );
  const hasEntries = prefetchedEntries.length > 0 || spotlightFooterRows.length > 0;

  const rankWidth = useMemo(() => {
    const ranks = prefetchedEntries.map((entry, index) => entry.rank ?? index + 1);
    for (const score of spotlightRows) ranks.push(score.rank);
    return computeRankWidth(ranks);
  }, [prefetchedEntries, spotlightRows]);

  const hasViewAllCounts = showLeaderboardEntryTotals === true && totalEntries != null && localEntries != null && totalEntries > 0;
  const fullViewAllLabel = hasViewAllCounts
    ? t('leaderboard.viewFullWithCounts', {
        local: localEntries!.toLocaleString(),
        total: totalEntries!.toLocaleString(),
      })
    : t('leaderboard.viewFullShort');
  const navigateToLeaderboard = useCallback(() => {
    leaderboardCache.delete(`${songId}:${instrument}`);
    navigate(`/songs/${songId}/${instrument}`, { state: { backTo: `/songs/${songId}` } });
  }, [instrument, navigate, songId]);
  const cardPress = useCardPressAction<HTMLDivElement>({ onPress: navigateToLeaderboard, disabled: !hasEntries });

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
        style={{ ...(hasEntries ? st.card : st.cardNoClick), ...(hasEntries && cardPress.isPressed ? st.cardPressed : undefined) }}
        role={hasEntries ? 'button' : undefined}
        tabIndex={hasEntries ? 0 : undefined}
        data-pressed={hasEntries && cardPress.isPressed ? 'true' : undefined}
        {...(hasEntries ? cardPress.pressHandlers : {})}
      >
        <div style={st.cardBody}>
        {prefetchedError && <span style={st.cardError}>{parseApiError(prefetchedError).title}</span>}
        {!prefetchedError && prefetchedEntries.length === 0 && spotlightFooterRows.length === 0 && (
          <div style={{ ...anim(baseDelay + STAGGER_ENTRY_OFFSET) }} onAnimationEnd={clearAnim}>
            <InstrumentEmptyState instrument={instrument} t={t} noMargin subtitleKey="songDetail.noScoresSubtitle" />
          </div>
        )}
        {!prefetchedError &&
          prefetchedEntries.map((entry, index) => {
            const rowStagger = anim(baseDelay + STAGGER_ENTRY_OFFSET + index * STAGGER_ROW_MS);
            const isPlayer = spotlightAccountIds.has(normalizeAccountId(entry.accountId));
            const rowStyle = { ...(isPlayer ? st.playerEntryRow : st.entryRow), ...(isCompactCard ? st.entryRowMobile : {}) };
            return (
            <InstrumentCardRowLink
              key={entry.accountId}
              id={isPlayer ? getSpotlightRowId(instrument, entry.accountId, playerAccountId) : undefined}
              to={`/player/${entry.accountId}`}
              state={{ backTo: `/songs/${songId}` }}
              style={{ ...rowStyle, ...rowStagger }}
              pressedStyle={st.entryRowPressed}
              onAnimationEnd={clearAnim} /* v8 ignore -- animation cleanup */
            >
              <LeaderboardEntry
                rank={entry.rank ?? index + 1}
                displayName={entry.displayName ?? entry.accountId.slice(0, 8)}
                score={entry.score}
                season={entry.season}
                accuracy={entry.accuracy}
                isFullCombo={!!entry.isFullCombo}
                isPlayer={isPlayer}
                difficulty={entry.difficulty}
                showDifficulty={showSeason}
                showSeason={showSeason}
                showAccuracy={showAccuracy}
                showInlineAccuracyAfterScore={showInlineAccuracyAfterScore}
                scoreWidth={effectiveScoreWidth}
                rankWidth={rankWidth}
              />
            </InstrumentCardRowLink>
            );
          })}
        {/* v8 ignore start — spotlight score rows; conditionally rendered animation block */}
        <CollapsePresence visible={spotlightFooterRows.length > 0}>
        {spotlightFooterRows.length > 0 && spotlightFooterRows.map((score, index) => {
          const playerDelay = baseDelay + STAGGER_ENTRY_OFFSET + (prefetchedEntries.length + index) * STAGGER_ROW_MS;
          const playerStagger = anim(playerDelay);
          const playerRowStyle = { ...st.playerEntryRow, ...(isCompactCard ? st.entryRowMobile : {}) };
          const isTrackedPlayerRow = playerAccountId != null && normalizeAccountId(score.accountId) === normalizeAccountId(playerAccountId);
          const playerLeaderboardPage = Math.floor(((score.localRank ?? score.rank) - 1) / 25) + 1;
          return (
            <InstrumentCardRowLink
              key={score.accountId}
              id={getSpotlightRowId(instrument, score.accountId, playerAccountId)}
              to={isTrackedPlayerRow
                ? `/songs/${songId}/${instrument}?page=${playerLeaderboardPage}&navToPlayer=true`
                : `/player/${score.accountId}`}
              state={isTrackedPlayerRow ? undefined : { backTo: `/songs/${songId}` }}
              style={{ ...playerRowStyle, ...playerStagger }}
              pressedStyle={st.entryRowPressed}
              onAnimationEnd={clearAnim} /* v8 ignore -- animation cleanup */
            >
              <LeaderboardEntry
                rank={score.rank}
                displayName={score.displayName}
                score={score.score}
                season={score.season}
                accuracy={score.accuracy}
                isFullCombo={!!score.isFullCombo}
                isPlayer
                difficulty={score.difficulty}
                showDifficulty={showSeason}
                showSeason={showSeason}
                showAccuracy={showAccuracy}
                showInlineAccuracyAfterScore={showInlineAccuracyAfterScore}
                scoreWidth={effectiveScoreWidth}
                rankWidth={rankWidth}
              />
            </InstrumentCardRowLink>
          );
        })}
        </CollapsePresence>
        {/* v8 ignore stop */}
        {/* v8 ignore start — view all IIFE; conditionally rendered animation block */}
        {!prefetchedError && prefetchedEntries.length > 0 && (() => {
          const viewAllDelay = baseDelay + STAGGER_ENTRY_OFFSET + (prefetchedEntries.length + spotlightFooterRows.length) * STAGGER_ROW_MS;
          const viewAllStagger = anim(viewAllDelay);
          const shouldUseCompactViewAll = hasViewAllCounts && (isCompactCard || viewAllNeedsCompact);
          if (shouldUseCompactViewAll) {
            return (
              <ViewFullLeaderboardCta
                compact
                componentRef={(element) => { viewAllButtonRef.current = element as HTMLDivElement | null; }}
                style={viewAllStagger}
                onAnimationEnd={clearAnim}
              >
                <span>{t('leaderboard.viewFullShort')}</span>
                <span>{t('leaderboard.trackedCount', { count: localEntries!.toLocaleString() as unknown as number })}</span>
                <span>{t('leaderboard.totalCount', { count: totalEntries!.toLocaleString() as unknown as number })}</span>
              </ViewFullLeaderboardCta>
            );
          }
          return (
            <ViewFullLeaderboardCta
              componentRef={(element) => { viewAllButtonRef.current = element as HTMLDivElement | null; }}
              style={viewAllStagger}
              onAnimationEnd={clearAnim}
            >
              {fullViewAllLabel}
            </ViewFullLeaderboardCta>
          );
        })()}
        {/* v8 ignore stop */}
      </div>
      </div>
    </div>
  );
});

function InstrumentCardRowLink({
  id,
  to,
  state,
  style,
  pressedStyle,
  onAnimationEnd,
  children,
}: {
  id?: string;
  to: string;
  state?: unknown;
  style: CSSProperties;
  pressedStyle: CSSProperties;
  onAnimationEnd?: AnimationEventHandler<HTMLAnchorElement>;
  children: ReactNode;
}) {
  const linkPress = useNavLinkPress<HTMLAnchorElement>({ to, state });

  const handleClick = useCallback<MouseEventHandler<HTMLAnchorElement>>((event) => {
    event.stopPropagation();
    linkPress.linkPressHandlers.onClick(event);
  }, [linkPress.linkPressHandlers]);

  return (
    <Link
      id={id}
      to={to}
      state={state}
      style={{ ...style, ...(linkPress.isPressed ? pressedStyle : undefined) }}
      data-pressed={linkPress.isPressed ? 'true' : undefined}
      onAnimationEnd={onAnimationEnd}
      {...linkPress.linkPressHandlers}
      onClick={handleClick}
    >
      {children}
    </Link>
  );
}

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
      transition: [
        transition(CssProp.backgroundColor, FAST_FADE_MS),
        transition(CssProp.borderColor, FAST_FADE_MS),
      ].join(', '),
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
      cardPressed: {
        backgroundColor: 'rgba(255, 255, 255, 0.04)',
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
      entryRowPressed: {
        backgroundColor: 'rgba(255, 255, 255, 0.06)',
      } as CSSProperties,
    };
  }, []);
}
