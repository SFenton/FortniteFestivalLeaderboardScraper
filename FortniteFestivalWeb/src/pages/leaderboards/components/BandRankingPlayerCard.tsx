/* eslint-disable react/forbid-dom-props -- shared card footer uses inline theme styles */
import { type AnimationEventHandler, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import type { BandRankingEntry, BandRankingMetric, BandType, PlayerBandEntry } from '@festival/core/api/serverTypes';
import { Gap } from '@festival/theme';
import { rankColor } from '@festival/core';
import PlayerBandCard, { formatPlayerBandNames } from '../../player/components/PlayerBandCard';
import { formatBayesianRatingDisplay, formatRankingValueDisplay, formatRating, getRatingPillTier } from '../helpers/rankingHelpers';
import { getBandBayesianRatingForMetric, getBandRankForMetric, getBandRatingForMetric, getBandSongsLabel } from '../helpers/bandRankingHelpers';
import { RankingMetadata } from './RankingEntry';

type BandRankingPlayerCardProps = {
  entry: BandRankingEntry;
  bandType: BandType;
  metric: BandRankingMetric;
  totalTeams?: number;
  sourceAccountId?: string;
  rankWidth?: number;
  testId?: string;
  style?: CSSProperties;
  onAnimationEnd?: AnimationEventHandler<HTMLElement>;
};

export default function BandRankingPlayerCard({
  entry,
  bandType,
  metric,
  totalTeams,
  sourceAccountId,
  rankWidth,
  testId,
  style,
  onAnimationEnd,
}: BandRankingPlayerCardProps) {
  const { t } = useTranslation();
  const playerBandEntry = bandRankingToPlayerBandEntry(entry, bandType);
  const names = formatPlayerBandNames(playerBandEntry);
  const rank = getBandRankForMetric(entry, metric);

  return (
    <PlayerBandCard
      entry={playerBandEntry}
      sourceAccountId={sourceAccountId}
      rank={rank}
      rankWidth={rankWidth}
      testId={testId}
      style={style}
      onAnimationEnd={onAnimationEnd}
      ariaLabel={names ? t('bandList.viewBand', { names }) : t('band.title')}
      scoreFooter={<BandRankingFooter entry={entry} metric={metric} rank={rank} totalTeams={totalTeams} />}
      scoreFooterAriaLabel={formatBandRankingFooterAria(entry, metric, rank)}
    />
  );
}

export function bandRankingToPlayerBandEntry(entry: BandRankingEntry, bandType: BandType): PlayerBandEntry {
  const members = entry.members && entry.members.length > 0
    ? entry.members
    : entry.teamMembers.map(member => ({
        accountId: member.accountId,
        displayName: member.displayName,
        instruments: [],
      }));

  return {
    bandId: entry.bandId,
    teamKey: entry.teamKey,
    bandType,
    members,
  };
}

function BandRankingFooter({ entry, metric, rank, totalTeams }: { entry: BandRankingEntry; metric: BandRankingMetric; rank: number; totalTeams?: number }) {
  const rating = getBandRatingForMetric(entry, metric);
  const bayesianRating = getBandBayesianRatingForMetric(entry, metric);

  return (
    <span data-testid="band-ranking-metadata" style={bandRankingFooterStyles.metadataRow}>
      <RankingMetadata
        ratingLabel={formatRating(rating, metric)}
        songsLabel={getBandSongsLabel(entry, metric)}
        percentileValueDisplay={formatRankingValueDisplay(rating, metric)}
        bayesianRankDisplay={formatBayesianRatingDisplay(bayesianRating, metric)}
        bayesianRankColor={totalTeams ? rankColor(rank, totalTeams) : undefined}
        ratingPillTier={getRatingPillTier(rating, metric)}
        songsLabelPrimary={metric === 'fcrate'}
        songsLabelGoldPrefix={metric === 'fcrate'}
        reserveTenDigitScoreWidth={metric === 'totalscore'}
      />
    </span>
  );
}

function getBandRankingValueLabel(entry: BandRankingEntry, metric: BandRankingMetric): string {
  const rating = getBandRatingForMetric(entry, metric);
  if (metric === 'adjusted' || metric === 'weighted') {
    const percentileLabel = formatRankingValueDisplay(rating, metric);
    const bayesianLabel = formatBayesianRatingDisplay(getBandBayesianRatingForMetric(entry, metric), metric);
    if (percentileLabel && bayesianLabel) return `${percentileLabel} · ${bayesianLabel}`;
    return percentileLabel ?? bayesianLabel ?? '-';
  }

  return formatRating(rating, metric) || '-';
}

function formatBandRankingFooterAria(entry: BandRankingEntry, metric: BandRankingMetric, rank: number): string {
  const coverage = getBandSongsLabel(entry, metric);
  const value = metric === 'totalscore' ? entry.totalScore.toLocaleString() : getBandRankingValueLabel(entry, metric);
  return `Rank ${rank.toLocaleString()}, ${coverage} songs, ${value}`;
}

const bandRankingFooterStyles = {
  metadataRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.xl,
    width: '100%',
    minWidth: 0,
  } as CSSProperties,
};