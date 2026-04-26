/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo, useRef, type CSSProperties } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { InstrumentHeaderSize } from '@festival/core';
import InstrumentHeader from '../../../components/display/InstrumentHeader';
import { RankingEntry } from './RankingEntry';
import type { ServerInstrumentKey as InstrumentKey, AccountRankingEntry, AccountRankingDto, RankingMetric } from '@festival/core/api/serverTypes';
import InstrumentEmptyState from '../../player/sections/InstrumentEmptyState';
import { Routes } from '../../../routes';
import { parseApiError } from '../../../utils/apiError';
import { getRankForMetric, formatRating, getRatingForMetric, getBayesianRatingForMetric, computeRankWidth, computePillMinWidth, getSongsLabel, formatBayesianRatingDisplay, formatRankingValueDisplay, getRatingPillTier, usesPercentileValueDisplay } from '../helpers/rankingHelpers';
import { useContainerWidth } from '../../../hooks/ui/useContainerWidth';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { rankColor } from '@festival/core';
import { staggerDelay } from '@festival/ui-utils';
import {
  Colors, Font, Weight, Gap, Radius, Layout,
  Display, Align, Justify, Overflow, Cursor, CssValue, CssProp,
  FAST_FADE_MS, STAGGER_INTERVAL, FADE_DURATION, frostedCard, flexColumn, flexRow, transition, padding, border, Border,
} from '@festival/theme';

const PERCENTILE_TWO_ROW_WIDTH_THRESHOLD = 680;
const PERCENTILE_TWO_ROW_HEIGHT = Layout.entryRowHeight + 28;

interface RankingCardProps {
  instrument: InstrumentKey;
  metric: RankingMetric;
  entries: AccountRankingEntry[];
  totalAccounts: number;
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
  totalAccounts,
  playerRanking,
  playerAccountId,
  error,
  shouldStagger,
  staggerOffset = 0,
}: RankingCardProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const st = useRankingCardStyles();
  const cardBodyRef = useRef<HTMLDivElement>(null);
  const cardBodyWidth = useContainerWidth(cardBodyRef);
  const isMobile = useIsMobile();
  const reserveTenDigitScoreWidth = metric === 'totalscore';
  const usePercentileMetric = usesPercentileValueDisplay(metric);
  const isNarrowPercentileCard = cardBodyWidth > 0 && cardBodyWidth < PERCENTILE_TWO_ROW_WIDTH_THRESHOLD;
  const useTwoRowPercentile = usePercentileMetric && (isMobile || isNarrowPercentileCard);
  const percentileRowHeight = useTwoRowPercentile ? PERCENTILE_TWO_ROW_HEIGHT : Layout.entryRowHeight;
  const twoRowStyle: CSSProperties | undefined = useTwoRowPercentile
    ? { height: percentileRowHeight, boxSizing: 'border-box' }
    : undefined;

  const playerInTop = !!(playerAccountId && entries.some(e => e.accountId === playerAccountId));

  // Use one shared rank width across the instrument card, including the player row.
  const rankWidth = useMemo(() => {
    const allRanks = entries.map(e => getRankForMetric(e, metric));
    if (playerRanking) {
      allRanks.push(getRankForMetric(playerRanking, metric));
    }
    return computeRankWidth(allRanks);
  }, [entries, metric, playerRanking]);

  const hasPlayerFooter = !!(playerRanking && !playerInTop);

  const percentileValueMinWidth = useMemo(() => {
    if (!usePercentileMetric) return undefined;
    const labels = entries.map(e => formatRankingValueDisplay(getRatingForMetric(e, metric), metric));
    if (playerRanking) labels.push(formatRankingValueDisplay(getRatingForMetric(playerRanking, metric), metric));
    return computePillMinWidth(labels);
  }, [entries, metric, playerRanking, usePercentileMetric]);

  const bayesianRankMinWidth = useMemo(() => {
    if (!usePercentileMetric) return undefined;
    const labels = entries.map(e => formatBayesianRatingDisplay(getBayesianRatingForMetric(e, metric), metric));
    if (playerRanking) labels.push(formatBayesianRatingDisplay(getBayesianRatingForMetric(playerRanking, metric), metric));
    return computePillMinWidth(labels);
  }, [entries, metric, playerRanking, usePercentileMetric]);

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
  const viewAllLabel = totalAccounts > 0
    ? t('rankings.viewAllRankingsWithCount', { count: totalAccounts, formattedCount: totalAccounts.toLocaleString() })
    : t('rankings.viewAllRankings');

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
      <div ref={cardBodyRef} style={st.cardBody}>
        {error && <span style={st.cardError}>{parseApiError(error).title}</span>}
        {!error && entries.length === 0 && (
          <InstrumentEmptyState instrument={instrument} t={t} noMargin titleKey="rankings.noRankings" subtitleKey="rankings.noRankingsSubtitle" />
        )}
        {!error && entries.map((e, i) => {
          const rank = getRankForMetric(e, metric);
          const isPlayer = e.accountId === playerAccountId;
          const usePercentile = usePercentileMetric;
          const isFcRate = metric === 'fcrate';
          const rating = getRatingForMetric(e, metric);
          const bayesianRating = getBayesianRatingForMetric(e, metric);
          const rowStyle = isPlayer ? st.playerEntryRow : st.entryRow;
          const delay = shouldStagger ? staggerDelay(i + 1 + staggerOffset, STAGGER_INTERVAL, totalStaggerItems) : undefined;
          const staggerStyle: CSSProperties | undefined = delay != null
            ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${delay}ms forwards` }
            : undefined;
          return (
            <Link
              key={e.accountId}
              to={`/player/${e.accountId}`}
              style={{ ...rowStyle, ...twoRowStyle, ...staggerStyle }}
              onAnimationEnd={(ev) => {
                const el = ev.currentTarget;
                el.style.opacity = '';
                el.style.animation = '';
              }}
            >
              <RankingEntry
                rank={rank}
                displayName={e.displayName ?? e.accountId.slice(0, 8)}
                ratingLabel={formatRating(rating, metric)}
                songsLabel={getSongsLabel(e, metric)}
                percentileValueDisplay={usePercentile ? formatRankingValueDisplay(rating, metric) : undefined}
                percentileValueMinWidth={percentileValueMinWidth}
                bayesianRankDisplay={usePercentile ? formatBayesianRatingDisplay(bayesianRating, metric) : undefined}
                bayesianRankColor={usePercentile ? rankColor(rank, totalAccounts) : undefined}
                bayesianRankMinWidth={bayesianRankMinWidth}
                twoRowPercentileMetadata={useTwoRowPercentile}
                ratingPillTier={getRatingPillTier(rating, metric)}
                songsLabelPrimary={isFcRate}
                songsLabelGoldPrefix={isFcRate}
                isPlayer={isPlayer}
                rankWidth={rankWidth}
                reserveTenDigitScoreWidth={reserveTenDigitScoreWidth}
              />
            </Link>
          );
        })}
        {playerRanking && !playerInTop && (() => {
          const rank = getRankForMetric(playerRanking, metric);
          const usePercentile = usePercentileMetric;
          const isFcRate = metric === 'fcrate';
          const rating = getRatingForMetric(playerRanking, metric);
          const bayesianRating = getBayesianRatingForMetric(playerRanking, metric);
          return (
            <Link
              to={`/player/${playerRanking.accountId}`}
              style={{ ...st.playerEntryRow, ...twoRowStyle, ...playerFooterStaggerStyle }}
              onAnimationEnd={(ev) => {
                const el = ev.currentTarget;
                el.style.opacity = '';
                el.style.animation = '';
              }}
            >
              <RankingEntry
                rank={rank}
                displayName={playerRanking.displayName ?? playerRanking.accountId.slice(0, 8)}
                ratingLabel={formatRating(rating, metric)}
                songsLabel={getSongsLabel(playerRanking, metric)}
                percentileValueDisplay={usePercentile ? formatRankingValueDisplay(rating, metric) : undefined}
                percentileValueMinWidth={percentileValueMinWidth}
                bayesianRankDisplay={usePercentile ? formatBayesianRatingDisplay(bayesianRating, metric) : undefined}
                bayesianRankColor={usePercentile ? rankColor(rank, totalAccounts) : undefined}
                bayesianRankMinWidth={bayesianRankMinWidth}
                twoRowPercentileMetadata={useTwoRowPercentile}
                ratingPillTier={getRatingPillTier(rating, metric)}
                songsLabelPrimary={isFcRate}
                songsLabelGoldPrefix={isFcRate}
                isPlayer
                rankWidth={rankWidth}
                reserveTenDigitScoreWidth={reserveTenDigitScoreWidth}
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
            {viewAllLabel}
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
