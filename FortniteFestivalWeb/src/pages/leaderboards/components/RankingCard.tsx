/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo, type CSSProperties } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { InstrumentHeaderSize } from '@festival/core';
import InstrumentHeader from '../../../components/display/InstrumentHeader';
import { RankingEntry } from './RankingEntry';
import type { ServerInstrumentKey as InstrumentKey, AccountRankingEntry, AccountRankingDto, RankingMetric } from '@festival/core/api/serverTypes';
import { Routes } from '../../../routes';
import { getRankForMetric, formatRating, getRatingForMetric, computeRankWidth } from '../helpers/rankingHelpers';
import { staggerDelay } from '@festival/ui-utils';
import {
  Colors, Font, Weight, Gap, Radius, Layout,
  Display, Align, Justify, Overflow, Cursor, CssValue, CssProp,
  FAST_FADE_MS, STAGGER_INTERVAL, FADE_DURATION, frostedCard, flexColumn, flexRow, transition, padding, border, Border,
} from '@festival/theme';

interface RankingCardProps {
  instrument: InstrumentKey;
  metric: RankingMetric;
  entries: AccountRankingEntry[];
  playerRanking?: AccountRankingDto | null;
  playerAccountId?: string;
  error?: string | null;
  shouldStagger?: boolean;
  staggerOffset?: number;
}

export default memo(function RankingCard({
  instrument,
  metric,
  entries,
  playerRanking,
  playerAccountId,
  error,
  shouldStagger,
  staggerOffset = 0,
}: RankingCardProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const st = useRankingCardStyles();

  const playerInTop = !!(playerAccountId && entries.some(e => e.accountId === playerAccountId));

  // Compute rank column width from the longest rank across all visible rows
  const rankWidth = useMemo(() => {
    const allRanks = entries.map(e => getRankForMetric(e, metric));
    if (playerRanking && !playerInTop) {
      allRanks.push(getRankForMetric(playerRanking, metric));
    }
    return computeRankWidth(allRanks);
  }, [entries, playerRanking, playerInTop, metric]);

  const hasPlayerFooter = !!(playerRanking && !playerInTop);
  const extraItems = hasPlayerFooter ? 3 : 2; // header + (player footer?) + button
  const totalStaggerItems = entries.length + extraItems + staggerOffset;
  const headerDelay = shouldStagger ? staggerDelay(0 + staggerOffset, STAGGER_INTERVAL, totalStaggerItems) : undefined;
  const headerStaggerStyle: CSSProperties | undefined = headerDelay != null
    ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${headerDelay}ms forwards` }
    : undefined;
  const playerFooterIdx = entries.length + 1 + staggerOffset;
  const playerFooterDelay = shouldStagger && hasPlayerFooter ? staggerDelay(playerFooterIdx, STAGGER_INTERVAL, totalStaggerItems) : undefined;
  const playerFooterStaggerStyle: CSSProperties | undefined = playerFooterDelay != null
    ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${playerFooterDelay}ms forwards` }
    : undefined;
  const buttonIdx = entries.length + (hasPlayerFooter ? 2 : 1) + staggerOffset;
  const buttonDelay = shouldStagger ? staggerDelay(buttonIdx, STAGGER_INTERVAL, totalStaggerItems) : undefined;
  const buttonStaggerStyle: CSSProperties | undefined = buttonDelay != null
    ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${buttonDelay}ms forwards` }
    : undefined;

  return (
    <div style={st.cardWrapper}>
      <div
        style={{ ...st.cardLabel, ...headerStaggerStyle }}
        onAnimationEnd={(ev) => {
          const el = ev.currentTarget;
          el.style.opacity = '';
          el.style.animation = '';
        }}
      >
        <InstrumentHeader instrument={instrument} size={InstrumentHeaderSize.MD} />
      </div>
      <div style={st.cardBody}>
        {error && <span style={st.cardError}>{error}</span>}
        {!error && entries.length === 0 && (
          <span style={st.cardMuted}>{t('rankings.noRankings')}</span>
        )}
        {!error && entries.map((e, i) => {
          const rank = getRankForMetric(e, metric);
          const isPlayer = e.accountId === playerAccountId;
          const rowStyle = isPlayer ? st.playerEntryRow : st.entryRow;
          const delay = shouldStagger ? staggerDelay(i + 1 + staggerOffset, STAGGER_INTERVAL, totalStaggerItems) : undefined;
          const staggerStyle: CSSProperties | undefined = delay != null
            ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${delay}ms forwards` }
            : undefined;
          return (
            <Link
              key={e.accountId}
              to={`/player/${e.accountId}`}
              style={{ ...rowStyle, ...staggerStyle }}
              onAnimationEnd={(ev) => {
                const el = ev.currentTarget;
                el.style.opacity = '';
                el.style.animation = '';
              }}
            >
              <RankingEntry
                rank={rank}
                displayName={e.displayName ?? e.accountId.slice(0, 8)}
                ratingLabel={formatRating(getRatingForMetric(e, metric), metric)}
                songsLabel={`${e.songsPlayed} / ${e.totalChartedSongs}`}
                isPlayer={isPlayer}
                rankWidth={rankWidth}
              />
            </Link>
          );
        })}
        {playerRanking && !playerInTop && (() => {
          const rank = getRankForMetric(playerRanking, metric);
          return (
            <Link
              to={`/player/${playerRanking.accountId}`}
              style={{ ...st.playerEntryRow, ...playerFooterStaggerStyle }}
              onAnimationEnd={(ev) => {
                const el = ev.currentTarget;
                el.style.opacity = '';
                el.style.animation = '';
              }}
            >
              <RankingEntry
                rank={rank}
                displayName={playerRanking.displayName ?? playerRanking.accountId.slice(0, 8)}
                ratingLabel={formatRating(getRatingForMetric(playerRanking, metric), metric)}
                songsLabel={`${playerRanking.songsPlayed} / ${playerRanking.totalChartedSongs}`}
                isPlayer
                rankWidth={rankWidth}
              />
            </Link>
          );
        })()}
        {!error && entries.length > 0 && (
          <div
            style={{ ...st.viewAllButton, ...buttonStaggerStyle }}
            onClick={() => navigate(Routes.fullRankings(instrument, metric))}
            onAnimationEnd={(ev) => {
              const el = ev.currentTarget;
              el.style.opacity = '';
              el.style.animation = '';
            }}
          >
            {t('rankings.viewAllRankings')}
          </div>
        )}
      </div>
    </div>
  );
});

function useRankingCardStyles() {
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
