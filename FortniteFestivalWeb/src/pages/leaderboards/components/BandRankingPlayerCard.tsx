/* eslint-disable react/forbid-dom-props -- shared card footer uses inline theme styles */
import { type AnimationEventHandler, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import type { BandConfiguration, BandRankingEntry, BandRankingMetric, BandType, PlayerBandEntry, PlayerBandMember, ServerInstrumentKey } from '@festival/core/api/serverTypes';
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
  activeFilterComboId?: string;
  activeFilterTeamKey?: string;
  activeFilterInstruments?: readonly ServerInstrumentKey[];
  activeFilterConfigurations?: readonly BandConfiguration[];
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
  activeFilterComboId,
  activeFilterTeamKey,
  activeFilterInstruments,
  activeFilterConfigurations,
  rankWidth,
  testId,
  style,
  onAnimationEnd,
}: BandRankingPlayerCardProps) {
  const { t } = useTranslation();
  const configurations = entry.configurations?.length
    ? entry.configurations
    : entry.teamKey === activeFilterTeamKey
      ? activeFilterConfigurations
      : undefined;
  const playerBandEntry = bandRankingToPlayerBandEntry(entry, bandType, activeFilterInstruments, activeFilterComboId, configurations);
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

export function bandRankingToPlayerBandEntry(entry: BandRankingEntry, bandType: BandType, activeFilterInstruments?: readonly ServerInstrumentKey[], activeFilterComboId?: string, configurations?: readonly BandConfiguration[]): PlayerBandEntry {
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
    members: resolveDisplayedMembers(members, bandType, activeFilterInstruments, activeFilterComboId, configurations ?? entry.configurations),
  };
}

function resolveDisplayedMembers(
  members: readonly PlayerBandMember[],
  bandType: BandType,
  activeFilterInstruments?: readonly ServerInstrumentKey[],
  activeFilterComboId?: string,
  configurations?: readonly BandConfiguration[],
): PlayerBandMember[] {
  if (bandType === 'Band_Duets' && activeFilterInstruments?.length) {
    const configuredMembers = buildDuosConfiguredMembers(members, configurations, activeFilterInstruments, activeFilterComboId);
    return configuredMembers ?? filterDuoMemberInstruments(members, activeFilterInstruments);
  }

  return filterMemberInstruments(members, activeFilterInstruments);
}

function buildDuosConfiguredMembers(
  members: readonly PlayerBandMember[],
  configurations: readonly BandConfiguration[] | undefined,
  activeFilterInstruments: readonly ServerInstrumentKey[],
  activeFilterComboId?: string,
): PlayerBandMember[] | null {
  if (members.length !== 2 || !configurations?.length) return null;

  const matchingConfigurations = activeFilterComboId
    ? configurations.filter(configuration => configuration.comboId === activeFilterComboId)
    : configurations;
  const memberInstrumentSets = members.map(() => new Set<ServerInstrumentKey>());
  const seenAssignments = new Set<string>();

  for (const configuration of matchingConfigurations) {
    const assignedInstruments = members.map(member => configuration.memberInstruments[member.accountId]);
    if (assignedInstruments.some(instrument => !instrument)) continue;

    const assignmentKey = assignedInstruments.map((instrument, index) => `${members[index]!.accountId}:${instrument}`).join('|');
    if (seenAssignments.has(assignmentKey)) continue;

    seenAssignments.add(assignmentKey);
    assignedInstruments.forEach((instrument, index) => memberInstrumentSets[index]!.add(instrument!));
  }

  if (memberInstrumentSets.some(instruments => instruments.size === 0)) return null;

  const configuredMembers = members.map((member, index) => ({
    ...member,
    instruments: orderFilteredInstruments(memberInstrumentSets[index]!, activeFilterInstruments),
  }));
  return constrainDuoMemberInstruments(configuredMembers, activeFilterInstruments);
}

function filterMemberInstruments(members: readonly PlayerBandMember[], activeFilterInstruments?: readonly ServerInstrumentKey[]): PlayerBandMember[] {
  if (!activeFilterInstruments?.length) return [...members];

  const allowed = new Set(activeFilterInstruments);
  return members.map(member => ({
    ...member,
    instruments: member.instruments.filter(instrument => allowed.has(instrument)),
  }));
}

function filterDuoMemberInstruments(members: readonly PlayerBandMember[], activeFilterInstruments: readonly ServerInstrumentKey[]): PlayerBandMember[] {
  return constrainDuoMemberInstruments(filterMemberInstruments(members, activeFilterInstruments), activeFilterInstruments);
}

function constrainDuoMemberInstruments(members: readonly PlayerBandMember[], activeFilterInstruments: readonly ServerInstrumentKey[]): PlayerBandMember[] {
  if (members.length !== 2) return [...members];

  const activeDistinctInstruments = Array.from(new Set(activeFilterInstruments));
  if (activeDistinctInstruments.length !== 2) {
    return members.map(member => ({
      ...member,
      instruments: orderFilteredInstruments(member.instruments, activeFilterInstruments),
    }));
  }

  const [firstInstrument, secondInstrument] = activeDistinctInstruments;
  const constrainedSets = members.map(member => new Set(member.instruments.filter(instrument => instrument === firstInstrument || instrument === secondInstrument)));
  constrainOtherDuoMember(constrainedSets, 0, 1, firstInstrument!, secondInstrument!);
  constrainOtherDuoMember(constrainedSets, 1, 0, firstInstrument!, secondInstrument!);

  return members.map((member, index) => ({
    ...member,
    instruments: orderFilteredInstruments(constrainedSets[index]!, activeFilterInstruments),
  }));
}

function constrainOtherDuoMember(memberInstrumentSets: Set<ServerInstrumentKey>[], fixedIndex: number, otherIndex: number, firstInstrument: ServerInstrumentKey, secondInstrument: ServerInstrumentKey) {
  const fixedInstruments = memberInstrumentSets[fixedIndex]!;
  const otherInstruments = memberInstrumentSets[otherIndex]!;
  if (fixedInstruments.size !== 1 || otherInstruments.size <= 1) return;

  const fixedInstrument = Array.from(fixedInstruments)[0]!;
  const remainingInstrument = fixedInstrument === firstInstrument ? secondInstrument : firstInstrument;
  if (otherInstruments.has(remainingInstrument)) {
    memberInstrumentSets[otherIndex] = new Set([remainingInstrument]);
  }
}

function orderFilteredInstruments(instruments: Iterable<ServerInstrumentKey>, activeFilterInstruments: readonly ServerInstrumentKey[]): ServerInstrumentKey[] {
  const instrumentSet = new Set(instruments);
  const orderedActiveInstruments = Array.from(new Set(activeFilterInstruments));
  return [
    ...orderedActiveInstruments.filter(instrument => instrumentSet.has(instrument)),
    ...Array.from(instrumentSet).filter(instrument => !orderedActiveInstruments.includes(instrument)),
  ];
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