/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useCallback, useMemo, useRef, type AnimationEventHandler, type CSSProperties, type ReactNode } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { InstrumentHeaderSize } from '@festival/core';
import InstrumentHeader from '../../../components/display/InstrumentHeader';
import CardPressable from '../../../components/common/CardPressable';
import { COMPACT_PERCENTILE_ROW_HEIGHT, RankingEntry } from './RankingEntry';
import type { ServerInstrumentKey as InstrumentKey, AccountRankingEntry, AccountRankingDto, RankingMetric } from '@festival/core/api/serverTypes';
import InstrumentEmptyState from '../../player/sections/InstrumentEmptyState';
import { Routes } from '../../../routes';
import { parseApiError } from '../../../utils/apiError';
import { getRankForMetric, formatRating, getRatingForMetric, getBayesianRatingForMetric, computeRankWidth, computePillMinWidth, getSongsLabel, formatBayesianRatingDisplay, formatRankingValueDisplay, getRatingPillTier, usesPercentileValueDisplay } from '../helpers/rankingHelpers';
import { useContainerWidth } from '../../../hooks/ui/useContainerWidth';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { useNavLinkPress } from '../../../hooks/navigation/useNavLinkPress';
import { rankColor } from '@festival/core';
import { staggerDelay } from '@festival/ui-utils';
import {
  Colors, Font, Weight, Gap, Radius, Layout,
  Display, Align, Justify, Overflow, Cursor, CssValue, CssProp,
  FAST_FADE_MS, STAGGER_INTERVAL, FADE_DURATION, frostedCard, flexColumn, flexRow, transition, padding, border, Border,
} from '@festival/theme';

const PERCENTILE_TWO_ROW_WIDTH_THRESHOLD = 680;

interface RankingCardProps {
  instrument: InstrumentKey;
  metric: RankingMetric;
  entries: AccountRankingEntry[];
  totalAccounts: number;
  playerRanking?: AccountRankingDto | null;
  playerAccountId?: string;
  spotlightRankings?: AccountRankingDto[];
  error?: string | null;
  shouldStagger?: boolean;
  staggerOffset?: number;
}

function normalizeAccountId(accountId: string | null | undefined): string {
  return accountId?.trim().toLowerCase() ?? '';
}

export default memo(function RankingCard({
  instrument,
  metric,
  entries,
  totalAccounts,
  playerRanking,
  playerAccountId,
  spotlightRankings,
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
  const percentileRowHeight = useTwoRowPercentile ? COMPACT_PERCENTILE_ROW_HEIGHT : Layout.entryRowHeight;
  const twoRowStyle: CSSProperties | undefined = useTwoRowPercentile
    ? { height: percentileRowHeight, boxSizing: 'border-box' }
    : undefined;

  const topAccountIds = useMemo(
    () => new Set(entries.map(entry => normalizeAccountId(entry.accountId))),
    [entries],
  );
  const spotlightRows = useMemo(() => {
    const rows: AccountRankingDto[] = [];
    const seen = new Set<string>();
    const addRow = (ranking: AccountRankingDto | null | undefined) => {
      if (!ranking) return;
      const normalizedAccountId = normalizeAccountId(ranking.accountId);
      if (!normalizedAccountId || seen.has(normalizedAccountId)) return;
      seen.add(normalizedAccountId);
      rows.push(ranking);
    };

    addRow(playerRanking);
    for (const ranking of spotlightRankings ?? []) addRow(ranking);
    return rows;
  }, [playerRanking, spotlightRankings]);
  const spotlightAccountIds = useMemo(
    () => {
      const accountIds = new Set(spotlightRows.map(ranking => normalizeAccountId(ranking.accountId)));
      if (playerAccountId) accountIds.add(normalizeAccountId(playerAccountId));
      return accountIds;
    },
    [playerAccountId, spotlightRows],
  );
  const spotlightFooterRows = useMemo(
    () => spotlightRows
      .filter(ranking => !topAccountIds.has(normalizeAccountId(ranking.accountId)))
      .sort((a, b) => getRankForMetric(a, metric) - getRankForMetric(b, metric)),
    [metric, spotlightRows, topAccountIds],
  );

  // Use one shared rank width across the instrument card, including the player row.
  const rankWidth = useMemo(() => {
    const allRanks = entries.map(entry => getRankForMetric(entry, metric));
    for (const ranking of spotlightRows) allRanks.push(getRankForMetric(ranking, metric));
    return computeRankWidth(allRanks);
  }, [entries, metric, spotlightRows]);

  const footerCount = spotlightFooterRows.length;
  const getPlayerRoute = useCallback((accountId: string) => normalizeAccountId(accountId) === normalizeAccountId(playerAccountId)
    ? Routes.statistics
    : Routes.player(accountId), [playerAccountId]);

  const percentileValueMinWidth = useMemo(() => {
    if (!usePercentileMetric) return undefined;
    const labels = entries.map(entry => formatRankingValueDisplay(getRatingForMetric(entry, metric), metric));
    for (const ranking of spotlightRows) labels.push(formatRankingValueDisplay(getRatingForMetric(ranking, metric), metric));
    return computePillMinWidth(labels);
  }, [entries, metric, spotlightRows, usePercentileMetric]);

  const bayesianRankMinWidth = useMemo(() => {
    if (!usePercentileMetric) return undefined;
    const labels = entries.map(entry => formatBayesianRatingDisplay(getBayesianRatingForMetric(entry, metric), metric));
    for (const ranking of spotlightRows) labels.push(formatBayesianRatingDisplay(getBayesianRatingForMetric(ranking, metric), metric));
    return computePillMinWidth(labels);
  }, [entries, metric, spotlightRows, usePercentileMetric]);

  const extraItems = footerCount + 2; // header + spotlight footer rows + button
  const totalStaggerItems = entries.length + extraItems + staggerOffset;
  const headerDelay = shouldStagger ? staggerDelay(0 + staggerOffset, STAGGER_INTERVAL, totalStaggerItems) : undefined;
  const headerStaggerStyle: CSSProperties | undefined = headerDelay != null
    ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${headerDelay}ms forwards` }
    : undefined;
  const buttonIdx = entries.length + footerCount + 1 + staggerOffset;
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
        {!error && entries.map((entry, index) => {
          const rank = getRankForMetric(entry, metric);
          const isPlayer = spotlightAccountIds.has(normalizeAccountId(entry.accountId));
          const usePercentile = usePercentileMetric;
          const isFcRate = metric === 'fcrate';
          const rating = getRatingForMetric(entry, metric);
          const bayesianRating = getBayesianRatingForMetric(entry, metric);
          const rowStyle = isPlayer ? st.playerEntryRow : st.entryRow;
          const delay = shouldStagger ? staggerDelay(index + 1 + staggerOffset, STAGGER_INTERVAL, totalStaggerItems) : undefined;
          const staggerStyle: CSSProperties | undefined = delay != null
            ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${delay}ms forwards` }
            : undefined;
          return (
            <PressableRankingLink
              key={entry.accountId}
              to={getPlayerRoute(entry.accountId)}
              style={{ ...rowStyle, ...twoRowStyle, ...staggerStyle }}
              pressedStyle={st.pressablePressed}
              onAnimationEnd={(ev) => {
                const el = ev.currentTarget;
                el.style.opacity = '';
                el.style.animation = '';
              }}
            >
              <RankingEntry
                rank={rank}
                displayName={entry.displayName ?? entry.accountId.slice(0, 8)}
                ratingLabel={formatRating(rating, metric)}
                songsLabel={getSongsLabel(entry, metric)}
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
            </PressableRankingLink>
          );
        })}
        {spotlightFooterRows.map((ranking, index) => {
          const rank = getRankForMetric(ranking, metric);
          const usePercentile = usePercentileMetric;
          const isFcRate = metric === 'fcrate';
          const rating = getRatingForMetric(ranking, metric);
          const bayesianRating = getBayesianRatingForMetric(ranking, metric);
          const footerDelay = shouldStagger
            ? staggerDelay(entries.length + 1 + index + staggerOffset, STAGGER_INTERVAL, totalStaggerItems)
            : undefined;
          const footerStaggerStyle: CSSProperties | undefined = footerDelay != null
            ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${footerDelay}ms forwards` }
            : undefined;
          return (
            <PressableRankingLink
              key={ranking.accountId}
              to={getPlayerRoute(ranking.accountId)}
              style={{ ...st.playerEntryRow, ...twoRowStyle, ...footerStaggerStyle }}
              pressedStyle={st.pressablePressed}
              onAnimationEnd={(ev) => {
                const el = ev.currentTarget;
                el.style.opacity = '';
                el.style.animation = '';
              }}
            >
              <RankingEntry
                rank={rank}
                displayName={ranking.displayName ?? ranking.accountId.slice(0, 8)}
                ratingLabel={formatRating(rating, metric)}
                songsLabel={getSongsLabel(ranking, metric)}
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
            </PressableRankingLink>
          );
        })}
        {!error && entries.length > 0 && (
          <CardPressable
            style={{ ...st.viewAllButton, ...buttonStaggerStyle }}
            pressedStyle={st.pressablePressed}
            onPress={() => navigate(Routes.fullRankings(instrument, metric))}
            onAnimationEnd={(ev) => {
              const el = ev.currentTarget;
              el.style.opacity = '';
              el.style.animation = '';
            }}
          >
            {viewAllLabel}
          </CardPressable>
        )}
      </div>
    </div>
  );
});

function PressableRankingLink({
  to,
  style,
  pressedStyle,
  onAnimationEnd,
  children,
}: {
  to: string;
  style: CSSProperties;
  pressedStyle: CSSProperties;
  onAnimationEnd?: AnimationEventHandler<HTMLAnchorElement>;
  children: ReactNode;
}) {
  const linkPress = useNavLinkPress<HTMLAnchorElement>({ to });

  return (
    <Link
      to={to}
      style={{ ...style, ...(linkPress.isPressed ? pressedStyle : undefined) }}
      data-pressed={linkPress.isPressed ? 'true' : undefined}
      onAnimationEnd={onAnimationEnd}
      {...linkPress.linkPressHandlers}
    >
      {children}
    </Link>
  );
}

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
      pressablePressed: {
        backgroundColor: 'rgba(255, 255, 255, 0.06)',
      } as CSSProperties,
    };
  }, []);
}
