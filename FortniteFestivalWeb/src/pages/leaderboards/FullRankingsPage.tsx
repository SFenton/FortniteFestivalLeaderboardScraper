/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useEffect, useState, useCallback, useMemo, useRef, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { IoOptions } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useQuery } from '@tanstack/react-query';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { useSettings } from '../../contexts/SettingsContext';
import { COMPACT_PERCENTILE_ROW_HEIGHT, RankingEntry } from './components/RankingEntry';
import { PaginatedLeaderboard } from '../../components/leaderboard/PaginatedLeaderboard';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { ActionPill } from '../../components/common/ActionPill';
import PageHeaderTransition from '../../components/common/PageHeaderTransition';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import type {
  ServerInstrumentKey as InstrumentKey,
  RankingMetric,
  RankingsPageResponse,
  SoloFamilyPageResponse,
  SoloFamilyRankingDto,
  SoloFamilyRankingEntry,
  SoloFamilyScopeId,
  ComboPageResponse,
  AccountRankingDto,
  ComboRankingEntry,
  AccountRankingEntry,
  BandRankingDto,
  SelectedMemberRankingsResponse,
} from '@festival/core/api/serverTypes';
import { InstrumentHeaderSize, rankColor } from '@festival/core';
import { serverInstrumentLabel, DEFAULT_INSTRUMENT, SOLO_FAMILY_SCOPE_IDS, soloFamilyScopeLabel } from '@festival/core/api/serverTypes';
import { LEADERBOARD_PAGE_SIZE, getRankForMetric, formatRating, getRatingForMetric, getSongsLabel, computeRankWidth, computePillMinWidth, formatBayesianRatingDisplay, formatRankingValueDisplay, getRatingPillTier, usesPercentileValueDisplay } from './helpers/rankingHelpers';
import { coerceBandRankingMetric, formatBandTeamName, getBandBayesianRatingForMetric, getBandRankForMetric, getBandRatingForMetric, getBandSongsLabel, getEnabledBandRankingMetrics } from './helpers/bandRankingHelpers';
import { loadLeaderboardRankBy, saveLeaderboardRankBy } from '../../utils/leaderboardSettings';
import { rankingsCache } from '../../api/pageCache';
import { useModalState } from '../../hooks/ui/useModalState';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useFabSearch } from '../../contexts/FabSearchContext';
import EmptyState from '../../components/common/EmptyState';
import { parseApiError } from '../../utils/apiError';
import { buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import { Size, Colors, Font, Weight, Layout, Gap, FADE_DURATION, DEMO_SWAP_INTERVAL_MS } from '@festival/theme';
import InstrumentPickerModal from './modals/InstrumentPickerModal';
import RankByModal from './modals/RankByModal';
import { comboScopeLabel, isRankingScopeComboId } from '../../utils/rankingScopes';
import { coerceRankingMetric } from './helpers/rankingHelpers';
import { useAppliedBandComboFilter } from '../../contexts/BandFilterActionContext';
import { Routes } from '../../routes';
import { isBandFilterForSelectedProfile } from '../../state/bandFilter';

type FullRankingsData = RankingsPageResponse | ComboPageResponse | SoloFamilyPageResponse;
type FullPlayerRanking = AccountRankingDto | SoloFamilyRankingDto | ({ comboId: string; rankBy: string; totalAccounts: number } & ComboRankingEntry);
type FullRankingEntry = AccountRankingEntry | ComboRankingEntry | SoloFamilyRankingEntry;

type SelectedBandMember = {
  accountId: string;
  displayName: string;
};

const EMPTY_RANKING_ENTRIES: FullRankingEntry[] = [];

export default function FullRankingsPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const { settings } = useSettings();

  const { profile, player } = useTrackedPlayer();
  const selectedBand = profile?.type === 'band' ? profile : null;

  const rawComboId = searchParams.get('combo');
  const comboId = rawComboId && isRankingScopeComboId(rawComboId) ? rawComboId : null;
  const rawFamilyScopeId = searchParams.get('family') as SoloFamilyScopeId | null;
  const familyScopeId = rawFamilyScopeId && SOLO_FAMILY_SCOPE_IDS.includes(rawFamilyScopeId) ? rawFamilyScopeId : null;
  const isCombo = comboId != null;
  const isFamily = !isCombo && familyScopeId != null;
  const useSelectedBandSoloFooter = !!selectedBand && !isCombo && !isFamily;
  const instrument = (searchParams.get('instrument') ?? 'Solo_Guitar') as InstrumentKey;
  const rawMetric = searchParams.get('rankBy') ?? loadLeaderboardRankBy();
  const metric = selectedBand ? coerceBandRankingMetric(rawMetric, true) : coerceRankingMetric(rawMetric, true);
  const bandMetric = coerceBandRankingMetric(metric, true);
  const pageParam = Math.max(1, Number(searchParams.get('page')) || 1);
  const appliedBandComboFilter = useAppliedBandComboFilter();
  const hasSelectedBandComboFilter = isBandFilterForSelectedProfile(appliedBandComboFilter, profile);
  const selectedBandComboId = isCombo
    ? comboId!
    : hasSelectedBandComboFilter && selectedBand && appliedBandComboFilter?.bandType === selectedBand.bandType
      ? appliedBandComboFilter.comboId
      : undefined;
  const isMobileChrome = useIsMobileChrome();
  const hasFab = isMobileChrome;
  const fabSearch = useFabSearch();
  const scrollContainerRef = useScrollContainer();

  const metricModal = useModalState<RankingMetric>(() => 'totalscore');
  const instrumentModal = useModalState<InstrumentKey>(() => DEFAULT_INSTRUMENT);

  const openMetricModal = useCallback(() => {
    metricModal.open(metric);
  }, [metricModal, metric]);

  const openInstrumentModal = useCallback(() => {
    instrumentModal.open(instrument);
  }, [instrumentModal, instrument]);

  const applyMetric = useCallback(() => {
    const nextMetric = selectedBand ? coerceBandRankingMetric(metricModal.draft, true) : coerceRankingMetric(metricModal.draft, true);
    metricModal.close();
    saveLeaderboardRankBy(nextMetric);
    scrollContainerRef.current?.scrollTo(0, 0);
    setPage(1);
    setSearchParams(
      isCombo
        ? { combo: comboId!, rankBy: nextMetric, page: '1' }
        : isFamily
          ? { family: familyScopeId!, rankBy: nextMetric, page: '1' }
        : { instrument, rankBy: nextMetric, page: '1' },
      { replace: true },
    );
  }, [comboId, familyScopeId, instrument, isCombo, isFamily, metricModal, scrollContainerRef, selectedBand, setSearchParams]);

  const applyInstrument = useCallback(() => {
    const nextInstrument = instrumentModal.draft;
    instrumentModal.close();
    scrollContainerRef.current?.scrollTo(0, 0);
    setPage(1);
    setSearchParams({ instrument: nextInstrument, rankBy: metric, page: '1' }, { replace: true });
  }, [instrumentModal, metric, scrollContainerRef, setSearchParams]);

  useEffect(() => {
    fabSearch.registerLeaderboardActions({ openMetric: openMetricModal, openInstrument: isCombo || isFamily ? undefined : openInstrumentModal });
    return () => fabSearch.registerLeaderboardActions(null);
  }, [fabSearch, isCombo, isFamily, openInstrumentModal, openMetricModal]);

  const cacheKey = isCombo ? `combo:${comboId}:${metric}` : isFamily ? `family:${familyScopeId}:${metric}` : `${instrument}:${metric}`;
  const cached = rankingsCache.get(cacheKey);
  const [page, setPage] = useState(cached?.page ?? pageParam);

  useEffect(() => {
    if (!selectedBand || rawMetric === metric) return;
    saveLeaderboardRankBy(metric);
    scrollContainerRef.current?.scrollTo(0, 0);
    setPage(1);
    setSearchParams(
      isCombo
        ? { combo: comboId!, rankBy: metric, page: '1' }
        : isFamily
          ? { family: familyScopeId!, rankBy: metric, page: '1' }
        : { instrument, rankBy: metric, page: '1' },
      { replace: true },
    );
  }, [comboId, familyScopeId, instrument, isCombo, isFamily, metric, rawMetric, scrollContainerRef, selectedBand, setSearchParams]);

  const { data, isFetching, error } = useQuery<FullRankingsData>({
    queryKey: isCombo
      ? queryKeys.comboRankings(comboId!, metric, page, LEADERBOARD_PAGE_SIZE)
      : isFamily
        ? queryKeys.soloFamilyRankings(familyScopeId!, metric, page, LEADERBOARD_PAGE_SIZE)
      : queryKeys.rankings(instrument, metric, page, LEADERBOARD_PAGE_SIZE),
    queryFn: () => isCombo
      ? api.getComboRankings(comboId!, metric, page, LEADERBOARD_PAGE_SIZE)
      : isFamily
        ? api.getSoloFamilyRankings(familyScopeId!, metric, page, LEADERBOARD_PAGE_SIZE)
      : api.getRankings(instrument, metric, page, LEADERBOARD_PAGE_SIZE),
    placeholderData: (previous, previousQuery) => {
      if (!previous || !previousQuery) return undefined;
      const [queryName, scopeValue, options] = previousQuery.queryKey as [string, string, { rankBy?: string }];
      if (isCombo) {
        return queryName === 'comboRankings' && scopeValue === comboId && options.rankBy === metric ? previous : undefined;
      }
      if (isFamily) {
        return queryName === 'soloFamilyRankings' && scopeValue === familyScopeId && options.rankBy === metric ? previous : undefined;
      }
      return queryName === 'rankings' && scopeValue === instrument && options.rankBy === metric ? previous : undefined;
    },
  });

  const { data: playerRanking } = useQuery<FullPlayerRanking>({
    queryKey: player
      ? (isCombo
          ? queryKeys.playerComboRanking(player.accountId, comboId!, metric)
          : isFamily
            ? queryKeys.playerSoloFamilyRanking(player.accountId, familyScopeId!, metric)
          : queryKeys.playerRanking(instrument, player.accountId, metric))
      : ['disabled'],
    queryFn: () => isCombo
      ? api.getPlayerComboRanking(player!.accountId, comboId!, metric)
      : isFamily
        ? api.getPlayerSoloFamilyRanking(player!.accountId, familyScopeId!, metric)
      : api.getPlayerRanking(instrument, player!.accountId, metric),
    enabled: !!player,
  });

  const { data: selectedBandRanking } = useQuery<BandRankingDto | null>({
    queryKey: selectedBand
      ? queryKeys.bandRanking(selectedBand.bandType, selectedBand.teamKey, selectedBandComboId, bandMetric)
      : ['bandRanking', 'selectedBand', 'disabled'],
    queryFn: () => api.getBandRanking(selectedBand!.bandType, selectedBand!.teamKey, selectedBandComboId, bandMetric),
    enabled: !!selectedBand && !useSelectedBandSoloFooter,
    retry: false,
  });

  const selectedBandMembers = useMemo<SelectedBandMember[]>(() => {
    if (!selectedBand) return [];

    const memberNames = new Map(selectedBand.members.map(member => [normalizeAccountId(member.accountId), member.displayName]));
    const seen = new Set<string>();
    return selectedBand.teamKey.split(':').flatMap(accountId => {
      const normalizedAccountId = normalizeAccountId(accountId);
      if (!normalizedAccountId || seen.has(normalizedAccountId)) return [];
      seen.add(normalizedAccountId);
      return [{
        accountId,
        displayName: memberNames.get(normalizedAccountId) || accountId.slice(0, 8),
      }];
    });
  }, [selectedBand]);

  const selectedBandMemberAccountIds = useMemo(() => selectedBandMembers.map(member => member.accountId), [selectedBandMembers]);

  const { data: selectedMemberRankings } = useQuery<SelectedMemberRankingsResponse>({
    queryKey: useSelectedBandSoloFooter && selectedBandMemberAccountIds.length > 0
      ? queryKeys.selectedMemberRankings(selectedBandMemberAccountIds, [instrument], metric)
      : ['selectedMemberRankings', 'fullRankingsFooter', 'disabled'],
    queryFn: () => api.getSelectedMemberRankings(selectedBandMemberAccountIds, [instrument], metric),
    enabled: useSelectedBandSoloFooter && selectedBandMemberAccountIds.length > 0,
    retry: false,
  });

  const selectedBandSoloFooterRankings = useMemo(() => {
    const instrumentPayload = selectedMemberRankings?.instruments.find(payload => payload.instrument === instrument);
    const byAccountId = new Map<string, AccountRankingDto>();
    for (const ranking of instrumentPayload?.entries ?? []) {
      byAccountId.set(normalizeAccountId(ranking.accountId), {
        ...ranking,
        instrument: ranking.instrument || instrument,
      });
    }

    return selectedBandMembers.flatMap(member => {
      const ranking = byAccountId.get(normalizeAccountId(member.accountId));
      if (!ranking) return [];
      return [{
        ...ranking,
        accountId: ranking.accountId || member.accountId,
        displayName: ranking.displayName || member.displayName,
      }];
    });
  }, [instrument, selectedBandMembers, selectedMemberRankings?.instruments]);

  const selectedBandFooterName = useMemo(() => {
    if (!selectedBand) return undefined;
    return formatBandTeamName(selectedBandRanking?.teamMembers ?? selectedBand.members, selectedBand.displayName);
  }, [selectedBand, selectedBandRanking?.teamMembers]);

  const selectedBandFooterRoute = useMemo(() => {
    if (!selectedBand || !selectedBandFooterName) return undefined;
    return Routes.band(selectedBand.bandId, {
      bandType: selectedBand.bandType,
      teamKey: selectedBand.teamKey,
      names: selectedBandFooterName,
    });
  }, [selectedBand, selectedBandFooterName]);

  const totalPages = data ? Math.ceil(data.totalAccounts / LEADERBOARD_PAGE_SIZE) : 0;
  const entries = data?.entries ?? EMPTY_RANKING_ENTRIES;
  const reserveTenDigitScoreWidth = metric === 'totalscore';
  const usePercentileMetric = usesPercentileValueDisplay(metric);

  useEffect(() => {
    rankingsCache.set(cacheKey, { page, scrollTop: 0 });
  }, [cacheKey, page]);

  const goToPage = useCallback((nextPage: number) => {
    if (nextPage < 1 || (totalPages && nextPage > totalPages)) return;
    scrollContainerRef.current?.scrollTo(0, 0);
    setPage(nextPage);
    setSearchParams(
      isCombo
        ? { combo: comboId!, rankBy: metric, page: String(nextPage) }
        : isFamily
          ? { family: familyScopeId!, rankBy: metric, page: String(nextPage) }
        : { instrument, rankBy: metric, page: String(nextPage) },
      { replace: true },
    );
  }, [comboId, familyScopeId, instrument, isCombo, isFamily, metric, scrollContainerRef, setSearchParams, totalPages]);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'ArrowLeft' && page > 1) goToPage(page - 1);
      if (event.key === 'ArrowRight' && totalPages && page < totalPages) goToPage(page + 1);
    }

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [goToPage, page, totalPages]);

  const isMobile = useIsMobile();
  const loading = isFetching && !data;
  const useTwoRowPercentile = isMobile && usePercentileMetric;
  const leaderboardRowHeight = useTwoRowPercentile ? COMPACT_PERCENTILE_ROW_HEIGHT : Layout.entryRowHeight;

  const scrollRef = useRef<HTMLDivElement>(null);
  const staggerRushRef = useRef<(() => void) | undefined>(undefined);

  const hasPlayerFooter = !selectedBand && !!playerRanking;
  const hasSelectedBandSoloFooter = useSelectedBandSoloFooter && selectedBandMembers.length > 0 && !!selectedMemberRankings;
  const hasBandFooter = hasSelectedBandSoloFooter || !!(selectedBand && selectedBandRanking && selectedBandFooterName && selectedBandFooterRoute);
  const hasFooter = hasPlayerFooter || hasBandFooter;

  const rankWidth = useMemo(() => {
    const allRanks = entries.map((entry) => getDisplayRank(entry, metric));
    if (!isMobile && playerRanking) {
      allRanks.push(getDisplayRank(playerRanking, metric));
    }
    if (!isMobile && selectedBandRanking) {
      allRanks.push(getBandRankForMetric(selectedBandRanking, bandMetric));
    }
    if (!isMobile) {
      for (const ranking of selectedBandSoloFooterRankings) {
        allRanks.push(getDisplayRank(ranking, metric));
      }
    }
    return computeRankWidth(allRanks);
  }, [bandMetric, entries, isMobile, metric, playerRanking, selectedBandRanking, selectedBandSoloFooterRankings]);

  const playerRankWidth = useMemo(() => {
    if (!playerRanking) return undefined;
    return computeRankWidth([getDisplayRank(playerRanking, metric)]);
  }, [metric, playerRanking]);

  const selectedBandRankWidth = useMemo(() => {
    if (!selectedBandRanking) return undefined;
    return computeRankWidth([getBandRankForMetric(selectedBandRanking, bandMetric)]);
  }, [bandMetric, selectedBandRanking]);

  const selectedBandMemberRankWidth = useMemo(() => {
    if (selectedBandSoloFooterRankings.length === 0) return undefined;
    return computeRankWidth(selectedBandSoloFooterRankings.map(ranking => getDisplayRank(ranking, metric)));
  }, [metric, selectedBandSoloFooterRankings]);

  const percentileValueMinWidth = useMemo(() => {
    if (!usePercentileMetric) return undefined;
    const labels = entries.map((entry) => formatRankingValueDisplay(getDisplayRating(entry, metric), metric));
    if (playerRanking) labels.push(formatRankingValueDisplay(getDisplayRating(playerRanking, metric), metric));
    if (selectedBandRanking) labels.push(formatRankingValueDisplay(getBandRatingForMetric(selectedBandRanking, bandMetric), bandMetric));
    for (const ranking of selectedBandSoloFooterRankings) {
      labels.push(formatRankingValueDisplay(getDisplayRating(ranking, metric), metric));
    }
    return computePillMinWidth(labels);
  }, [bandMetric, entries, metric, playerRanking, selectedBandRanking, selectedBandSoloFooterRankings, usePercentileMetric]);

  const bayesianRankMinWidth = useMemo(() => {
    if (!usePercentileMetric) return undefined;
    const labels = entries.map((entry) => formatBayesianRatingDisplay(getDisplayBayesianRating(entry, metric), metric));
    if (playerRanking) labels.push(formatBayesianRatingDisplay(getDisplayBayesianRating(playerRanking, metric), metric));
    if (selectedBandRanking) labels.push(formatBayesianRatingDisplay(getBandBayesianRatingForMetric(selectedBandRanking, bandMetric), bandMetric));
    for (const ranking of selectedBandSoloFooterRankings) {
      labels.push(formatBayesianRatingDisplay(getDisplayBayesianRating(ranking, metric), metric));
    }
    return computePillMinWidth(labels);
  }, [bandMetric, entries, metric, playerRanking, selectedBandRanking, selectedBandSoloFooterRankings, usePercentileMetric]);

  const pageLabel = isCombo ? comboScopeLabel(comboId!) : isFamily ? soloFamilyScopeLabel(familyScopeId!) : serverInstrumentLabel(instrument);
  const showMobilePageHeader = !isMobileChrome || settings.showButtonsInHeaderMobile;

  return (
    <Page
      scrollRef={scrollRef}
      staggerRushRef={staggerRushRef}
      scrollRestoreKey={`rankings:${cacheKey}:${page}`}
      fabSpacer="none"
      before={isMobileChrome ? (
        <PageHeaderTransition visible={showMobilePageHeader}>
          <PageHeader title={renderPageTitle(isCombo || isFamily, pageLabel, t('rankings.title'), data?.totalAccounts, t, isFamily ? undefined : instrument)} />
        </PageHeaderTransition>
      ) : showMobilePageHeader ? (
        <PageHeader
          title={renderPageTitle(isCombo || isFamily, pageLabel, t('rankings.title'), data?.totalAccounts, t, isFamily ? undefined : instrument)}
          actions={
            !isMobileChrome ? (
              <>
                {!isCombo && !isFamily && (
                  <ActionPill
                    icon={<InstrumentIcon instrument={instrument} size={Size.iconAction} />}
                    label={serverInstrumentLabel(instrument)}
                    onClick={openInstrumentModal}
                    active={instrument !== DEFAULT_INSTRUMENT}
                  />
                )}
                <ActionPill
                  icon={<IoOptions size={Size.iconAction} />}
                  label={t(`rankings.metric.${metric}`)}
                  onClick={openMetricModal}
                  active={metric !== 'totalscore'}
                />
              </>
            ) : undefined
          }
        />
      ) : undefined}
      after={<>
        {!isCombo && !isFamily && (
          <InstrumentPickerModal
            visible={instrumentModal.visible}
            draft={instrumentModal.draft}
            savedDraft={instrument}
            onChange={instrumentModal.setDraft}
            onCancel={instrumentModal.close}
            onApply={applyInstrument}
          />
        )}
        <RankByModal
          visible={metricModal.visible}
          draft={metricModal.draft}
          onDraftChange={metricModal.setDraft}
          onClose={metricModal.close}
          onApply={applyMetric}
          onReset={metricModal.reset}
          experimentalRanksEnabled={true}
          metrics={selectedBand ? getEnabledBandRankingMetrics(true) : undefined}
          subject={selectedBand ? 'bands' : 'players'}
        />
      </>}
    >
      {error && (() => {
        const parsed = parseApiError(String(error));
        return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
      })()}

      <PaginatedLeaderboard<AccountRankingEntry | ComboRankingEntry | SoloFamilyRankingEntry>
        entries={entries}
        page={page}
        totalPages={totalPages}
        onGoToPage={goToPage}
        entryKey={(entry) => entry.accountId}
        isPlayerEntry={(entry) => entry.accountId === player?.accountId}
        renderRow={(entry) => {
          const rank = getDisplayRank(entry, metric);
          const rating = getDisplayRating(entry, metric);
          const usePercentile = usePercentileMetric;
          const isFcRate = metric === 'fcrate';
          const bayesianRating = getDisplayBayesianRating(entry, metric);

          return (
            <RankingEntry
              rank={rank}
              displayName={entry.displayName ?? entry.accountId.slice(0, 8)}
              ratingLabel={formatRating(rating, metric)}
              songsLabel={getDisplaySongsLabel(entry, metric)}
              percentileValueDisplay={usePercentile ? formatRankingValueDisplay(rating, metric) : undefined}
              percentileValueMinWidth={percentileValueMinWidth}
              bayesianRankDisplay={usePercentile ? formatBayesianRatingDisplay(bayesianRating, metric) : undefined}
              bayesianRankColor={usePercentile ? rankColor(rank, data?.totalAccounts ?? 0) : undefined}
              bayesianRankMinWidth={bayesianRankMinWidth}
              twoRowPercentileMetadata={useTwoRowPercentile}
              ratingPillTier={getRatingPillTier(rating, metric)}
              songsLabelPrimary={isFcRate}
              songsLabelGoldPrefix={isFcRate}
              isPlayer={entry.accountId === player?.accountId}
              rankWidth={rankWidth}
              reserveTenDigitScoreWidth={reserveTenDigitScoreWidth}
            />
          );
        }}
        entryLinkTo={(entry) => `/player/${entry.accountId}`}
        hasPlayerFooter={hasFooter}
        renderPlayerFooter={hasSelectedBandSoloFooter ? ({ className, style }) => (
          <SelectedBandMemberRotatingFooter
            rankings={selectedBandSoloFooterRankings}
            metric={metric}
            totalAccounts={data?.totalAccounts ?? 0}
            className={className}
            style={style}
            percentileValueMinWidth={percentileValueMinWidth}
            bayesianRankMinWidth={bayesianRankMinWidth}
            twoRowPercentileMetadata={useTwoRowPercentile}
            rankWidth={isMobile ? selectedBandMemberRankWidth : rankWidth}
            reserveTenDigitScoreWidth={reserveTenDigitScoreWidth && !(isMobile && hasFab)}
            cycleKey={`${selectedBand?.teamKey ?? ''}:${instrument}:${metric}:${selectedBandSoloFooterRankings.map(ranking => normalizeAccountId(ranking.accountId)).join(':')}`}
          />
        ) : selectedBand && selectedBandRanking && selectedBandFooterRoute && selectedBandFooterName ? ({ className, style }) => (
          <Link to={selectedBandFooterRoute} className={className} style={style}>
            <RankingEntry
              rank={getBandRankForMetric(selectedBandRanking, bandMetric)}
              displayName={selectedBandFooterName}
              ratingLabel={formatRating(getBandRatingForMetric(selectedBandRanking, bandMetric), bandMetric)}
              songsLabel={getBandSongsLabel(selectedBandRanking, bandMetric)}
              percentileValueDisplay={usesPercentileValueDisplay(bandMetric) ? formatRankingValueDisplay(getBandRatingForMetric(selectedBandRanking, bandMetric), bandMetric) : undefined}
              percentileValueMinWidth={percentileValueMinWidth}
              bayesianRankDisplay={usesPercentileValueDisplay(bandMetric) ? formatBayesianRatingDisplay(getBandBayesianRatingForMetric(selectedBandRanking, bandMetric), bandMetric) : undefined}
              bayesianRankColor={usesPercentileValueDisplay(bandMetric) ? rankColor(getBandRankForMetric(selectedBandRanking, bandMetric), selectedBandRanking.totalRankedTeams) : undefined}
              bayesianRankMinWidth={bayesianRankMinWidth}
              twoRowPercentileMetadata={useTwoRowPercentile}
              ratingPillTier={getRatingPillTier(getBandRatingForMetric(selectedBandRanking, bandMetric), bandMetric)}
              songsLabelPrimary={bandMetric === 'fcrate'}
              songsLabelGoldPrefix={bandMetric === 'fcrate'}
              isPlayer
              rankWidth={isMobile ? selectedBandRankWidth : rankWidth}
              reserveTenDigitScoreWidth={reserveTenDigitScoreWidth && !(isMobile && hasFab)}
            />
          </Link>
        ) : playerRanking ? ({ className, style }) => (
          <Link to={`/player/${playerRanking.accountId}`} className={className} style={style}>
            <RankingEntry
              rank={getDisplayRank(playerRanking, metric)}
              displayName={playerRanking.displayName ?? playerRanking.accountId.slice(0, 8)}
              ratingLabel={formatRating(getDisplayRating(playerRanking, metric), metric)}
              songsLabel={getDisplaySongsLabel(playerRanking, metric)}
              percentileValueDisplay={usesPercentileValueDisplay(metric) ? formatRankingValueDisplay(getDisplayRating(playerRanking, metric), metric) : undefined}
              percentileValueMinWidth={percentileValueMinWidth}
              bayesianRankDisplay={usesPercentileValueDisplay(metric) ? formatBayesianRatingDisplay(getDisplayBayesianRating(playerRanking, metric), metric) : undefined}
              bayesianRankColor={usesPercentileValueDisplay(metric) ? rankColor(getDisplayRank(playerRanking, metric), data?.totalAccounts ?? 0) : undefined}
              bayesianRankMinWidth={bayesianRankMinWidth}
              twoRowPercentileMetadata={useTwoRowPercentile}
              ratingPillTier={getRatingPillTier(getDisplayRating(playerRanking, metric), metric)}
              songsLabelPrimary={metric === 'fcrate'}
              songsLabelGoldPrefix={metric === 'fcrate'}
              isPlayer
              rankWidth={isMobile ? playerRankWidth : rankWidth}
              reserveTenDigitScoreWidth={reserveTenDigitScoreWidth && !(isMobile && hasFab)}
            />
          </Link>
        ) : undefined}
        loading={loading}
        cached={!!cached}
        isMobile={isMobile}
        hasFab={hasFab}
        rowHeight={leaderboardRowHeight}
        staggerRushRef={staggerRushRef}
        footerAnimKey={cacheKey}
      />
    </Page>
  );
}

function SelectedBandMemberRotatingFooter({
  rankings,
  metric,
  totalAccounts,
  className,
  style,
  percentileValueMinWidth,
  bayesianRankMinWidth,
  twoRowPercentileMetadata,
  rankWidth,
  reserveTenDigitScoreWidth,
  cycleKey,
}: {
  rankings: AccountRankingDto[];
  metric: RankingMetric;
  totalAccounts: number;
  className: string;
  style: CSSProperties;
  percentileValueMinWidth?: number;
  bayesianRankMinWidth?: number;
  twoRowPercentileMetadata?: boolean;
  rankWidth?: number;
  reserveTenDigitScoreWidth?: boolean;
  cycleKey: string;
}) {
  const [activeIndex, setActiveIndex] = useState(0);
  const [fading, setFading] = useState(false);

  useEffect(() => {
    setActiveIndex(0);
    setFading(false);
  }, [cycleKey]);

  useEffect(() => {
    if (rankings.length <= 1) return;

    let fadeTimer: ReturnType<typeof setTimeout> | undefined;
    const reduceMotion = prefersReducedMotion();
    const interval = setInterval(() => {
      if (reduceMotion) {
        setActiveIndex(index => (index + 1) % rankings.length);
        return;
      }

      setFading(true);
      fadeTimer = setTimeout(() => {
        setActiveIndex(index => (index + 1) % rankings.length);
        setFading(false);
      }, FADE_DURATION);
    }, DEMO_SWAP_INTERVAL_MS);

    return () => {
      clearInterval(interval);
      if (fadeTimer) clearTimeout(fadeTimer);
    };
  }, [cycleKey, rankings.length]);

  if (rankings.length === 0) {
    return (
      <div className={className} style={{ ...style, ...selectedBandMemberFooterStyles.shell }} data-testid="selected-band-member-footer-empty">
        <div style={selectedBandMemberFooterStyles.emptyContent}>No ranked band members for this instrument</div>
      </div>
    );
  }

  const activeRanking = rankings[Math.min(activeIndex, rankings.length - 1)]!;
  const rating = getDisplayRating(activeRanking, metric);
  const rank = getDisplayRank(activeRanking, metric);
  const usePercentile = usesPercentileValueDisplay(metric);
  const contentStyle = prefersReducedMotion()
    ? selectedBandMemberFooterStyles.content
    : { ...selectedBandMemberFooterStyles.content, opacity: fading ? 0 : 1 };

  return (
    <Link
      to={`/player/${activeRanking.accountId}`}
      className={className}
      style={{ ...style, ...selectedBandMemberFooterStyles.shell }}
      data-testid="selected-band-member-footer"
    >
      <div style={contentStyle} data-testid="selected-band-member-footer-content">
        <RankingEntry
          rank={rank}
          displayName={activeRanking.displayName ?? activeRanking.accountId.slice(0, 8)}
          ratingLabel={formatRating(rating, metric)}
          songsLabel={getDisplaySongsLabel(activeRanking, metric)}
          percentileValueDisplay={usePercentile ? formatRankingValueDisplay(rating, metric) : undefined}
          percentileValueMinWidth={percentileValueMinWidth}
          bayesianRankDisplay={usePercentile ? formatBayesianRatingDisplay(getDisplayBayesianRating(activeRanking, metric), metric) : undefined}
          bayesianRankColor={usePercentile ? rankColor(rank, activeRanking.totalRankedAccounts || totalAccounts) : undefined}
          bayesianRankMinWidth={bayesianRankMinWidth}
          twoRowPercentileMetadata={twoRowPercentileMetadata}
          ratingPillTier={getRatingPillTier(rating, metric)}
          songsLabelPrimary={metric === 'fcrate'}
          songsLabelGoldPrefix={metric === 'fcrate'}
          isPlayer
          rankWidth={rankWidth}
          reserveTenDigitScoreWidth={reserveTenDigitScoreWidth}
        />
      </div>
    </Link>
  );
}

function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
}

function normalizeAccountId(accountId: string | null | undefined): string {
  return accountId?.trim().toLowerCase() ?? '';
}

function renderPageTitle(
  isCombo: boolean,
  pageLabel: string,
  rankingsTitle: string,
  totalAccounts: number | undefined,
  t: ReturnType<typeof useTranslation>['t'],
  instrument?: InstrumentKey,
) {
  if (!isCombo && instrument) {
    return (
      <InstrumentHeader
        instrument={instrument}
        size={InstrumentHeaderSize.MD}
        label={`${pageLabel} ${rankingsTitle}`}
        subtitle={totalAccounts != null ? t('rankings.totalRanked', { count: totalAccounts, formattedCount: totalAccounts.toLocaleString() }) : undefined}
      />
    );
  }

  return (
    <div style={comboTitleStyles.wrapper}>
      <span style={comboTitleStyles.title}>{`${pageLabel} ${rankingsTitle}`}</span>
      {totalAccounts != null && (
        <span style={comboTitleStyles.subtitle}>{t('rankings.totalRanked', { count: totalAccounts, formattedCount: totalAccounts.toLocaleString() })}</span>
      )}
    </div>
  );
}

function isComboRankingEntry(
  entry: AccountRankingEntry | ComboRankingEntry | FullPlayerRanking,
): entry is ComboRankingEntry | ({ comboId: string; rankBy: string; totalAccounts: number } & ComboRankingEntry) {
  return 'rank' in entry && 'adjustedRating' in entry;
}

function getDisplayRank(entry: AccountRankingEntry | ComboRankingEntry | FullPlayerRanking, metric: RankingMetric): number {
  return isComboRankingEntry(entry) ? entry.rank : getRankForMetric(entry, metric);
}

function getDisplayRating(entry: AccountRankingEntry | ComboRankingEntry | FullPlayerRanking, metric: RankingMetric): number {
  if (!isComboRankingEntry(entry)) {
    return getRatingForMetric(entry, metric);
  }

  switch (metric) {
    case 'adjusted':
      return entry.adjustedRating;
    case 'weighted':
      return entry.weightedRating;
    case 'fcrate':
      return entry.fcRate;
    case 'totalscore':
      return entry.totalScore;
    case 'maxscore':
      return entry.maxScorePercent;
  }
}

function getDisplayBayesianRating(entry: AccountRankingEntry | ComboRankingEntry | FullPlayerRanking, metric: RankingMetric): number | undefined {
  if (!isComboRankingEntry(entry)) {
    switch (metric) {
      case 'adjusted': return entry.adjustedSkillRating;
      case 'weighted': return entry.weightedRating;
      default: return undefined;
    }
  }

  switch (metric) {
    case 'adjusted': return entry.adjustedRating;
    case 'weighted': return entry.weightedRating;
    default: return undefined;
  }
}

function getDisplaySongsLabel(entry: AccountRankingEntry | ComboRankingEntry | FullPlayerRanking, metric: RankingMetric): string | undefined {
  if (!isComboRankingEntry(entry)) {
    return getSongsLabel(entry, metric);
  }

  if (metric === 'fcrate') {
    return `${entry.fullComboCount} / ${entry.songsPlayed}`;
  }

  return `${entry.songsPlayed}`;
}

const comboTitleStyles = {
  wrapper: {
    display: 'flex',
    flexDirection: 'column',
    gap: 4,
  },
  title: {
    color: Colors.textPrimary,
    fontSize: Font.xl,
    fontWeight: Weight.bold,
  },
  subtitle: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
  },
} as const;

const selectedBandMemberFooterStyles = {
  shell: {
    overflow: 'hidden',
  },
  content: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    width: '100%',
    minWidth: 0,
    transition: `opacity ${FADE_DURATION}ms ease-in-out`,
  },
  emptyContent: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '100%',
    minWidth: 0,
    color: Colors.textSecondary,
    fontSize: Font.sm,
    fontWeight: Weight.semibold,
    textAlign: 'center',
  },
} satisfies Record<string, CSSProperties>;
