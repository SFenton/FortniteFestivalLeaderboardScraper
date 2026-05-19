/* eslint-disable react/forbid-dom-props -- page-level dynamic styles use inline style objects */
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { IoOptions } from 'react-icons/io5';
import type { BandRankingMetric } from '@festival/core/api/serverTypes';
import { LoadPhase, rankColor } from '@festival/core';
import { Colors, Display, Font, Gap, Size, Weight, flexColumn } from '@festival/theme';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import EmptyState from '../../components/common/EmptyState';
import PageHeader from '../../components/common/PageHeader';
import PageHeaderTransition from '../../components/common/PageHeaderTransition';
import { ActionPill } from '../../components/common/ActionPill';
import BandFilterPill from '../../components/common/BandFilterPill';
import { FixedLeaderboardPagination, FixedLeaderboardPlayerFooter, useLeaderboardFooterScrollMargin } from '../../components/leaderboard/LeaderboardPaginationFooter';
import Page from '../Page';
import { PageMessage } from '../PageMessage';
import RankByModal from './modals/RankByModal';
import BandComboFilterModal from './modals/BandComboFilterModal';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useAppliedBandComboFilter } from '../../contexts/BandFilterActionContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useSelectedProfile } from '../../hooks/data/useSelectedProfile';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useModalState } from '../../hooks/ui/useModalState';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useSettings } from '../../contexts/SettingsContext';
import { useStagger } from '../../hooks/ui/useStagger';
import { bandTypeLabel, coerceBandType } from '../../utils/bandTypes';
import { parseApiError } from '../../utils/apiError';
import { loadLeaderboardRankBy, saveLeaderboardRankBy } from '../../utils/leaderboardSettings';
import { isBandFilterForSelectedProfile } from '../../state/bandFilter';
import { formatPageBandComboLabel, getPageBandComboInstruments, PAGE_BAND_COMBO_ALL_VALUE, resolvePageBandComboState } from '../../utils/pageBandComboFilter';
import { computeRankWidth, formatBayesianRatingDisplay, formatRankingValueDisplay, formatRating, getRatingPillTier, LEADERBOARD_PAGE_SIZE, usesPercentileValueDisplay } from './helpers/rankingHelpers';
import { coerceBandRankingMetric, formatBandTeamName, getBandBayesianRatingForMetric, getBandRankForMetric, getBandRatingForMetric, getBandSongsLabel, getEnabledBandRankingMetrics } from './helpers/bandRankingHelpers';
import BandRankingPlayerCard from './components/BandRankingPlayerCard';
import { RankingEntry } from './components/RankingEntry';
import { Routes } from '../../routes';
import { getBandProfileRoute } from '../../utils/profileNavigation';

const COMBO_CATALOG_STALE_TIME_MS = 10 * 60_000;

export default function BandRankingsPage() {
  const { t } = useTranslation();
  const { bandType: rawBandType } = useParams<{ bandType: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const { settings } = useSettings();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const hasFab = isMobileChrome;
  const { registerLeaderboardActions } = useFabSearch();
  const scrollContainerRef = useScrollContainer();
  const bandType = coerceBandType(rawBandType);
  const { profile } = useSelectedProfile();
  const selectedBand = profile?.type === 'band' ? profile : null;
  const appliedBandComboFilter = useAppliedBandComboFilter();
  const pageBandComboState = useMemo(
    () => resolvePageBandComboState(bandType, searchParams, appliedBandComboFilter),
    [appliedBandComboFilter, bandType, searchParams],
  );
  const activeComboId = pageBandComboState.comboId;
  const selectedPlayerAccountId = profile?.type === 'player' ? profile.accountId : undefined;
  const selectedBandTeamKey = profile?.type === 'band' && profile.bandType === bandType ? profile.teamKey : undefined;
  const selectedBandHasGlobalFilter = isBandFilterForSelectedProfile(appliedBandComboFilter, profile);
  const selectedBandMatchesView = selectedBand?.bandType === bandType;
  const experimentalRanksEnabled = settings.enableExperimentalRanks;
  const rawMetric = searchParams.get('rankBy') ?? loadLeaderboardRankBy();
  const metric = coerceBandRankingMetric(rawMetric, experimentalRanksEnabled);
  const pageParam = Math.max(1, Number(searchParams.get('page')) || 1);
  const metricModal = useModalState<BandRankingMetric>(() => 'totalscore');
  const [comboModalOpen, setComboModalOpen] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);
  const staggerRushRef = useRef<(() => void) | undefined>(undefined);
  const openMetricDraft = metricModal.open;

  const openMetricModal = useCallback(() => {
    openMetricDraft(metric);
  }, [metric, openMetricDraft]);

  const applyMetric = useCallback(() => {
    if (!bandType) return;
    const nextMetric = coerceBandRankingMetric(metricModal.draft, experimentalRanksEnabled);
    metricModal.close();
    saveLeaderboardRankBy(nextMetric);
    scrollContainerRef.current?.scrollTo(0, 0);
    const params = new URLSearchParams(searchParams);
    params.set('rankBy', nextMetric);
    params.set('page', '1');
    setSearchParams(params, { replace: true });
  }, [bandType, experimentalRanksEnabled, metricModal, scrollContainerRef, searchParams, setSearchParams]);

  useEffect(() => {
    if (!bandType || rawMetric === metric) return;
    saveLeaderboardRankBy(metric);
    scrollContainerRef.current?.scrollTo(0, 0);
    const params = new URLSearchParams(searchParams);
    params.set('rankBy', metric);
    params.set('page', '1');
    setSearchParams(params, { replace: true });
  }, [bandType, metric, rawMetric, scrollContainerRef, searchParams, setSearchParams]);

  const comboCatalogQuery = useQuery({
    queryKey: queryKeys.bandRankingCombos(bandType ?? 'unknown'),
    queryFn: () => api.getBandRankingCombos(bandType!),
    enabled: !!bandType,
    staleTime: COMBO_CATALOG_STALE_TIME_MS,
  });

  const activeComboInstruments = useMemo(
    () => getPageBandComboInstruments(pageBandComboState, bandType, appliedBandComboFilter, comboCatalogQuery.data?.combos),
    [appliedBandComboFilter, bandType, comboCatalogQuery.data?.combos, pageBandComboState],
  );
  const comboFilterLabel = activeComboInstruments.length > 0
    ? formatPageBandComboLabel(activeComboInstruments)
    : t('bandComboFilter.actionLabel');

  const openComboModal = useCallback(() => setComboModalOpen(true), []);
  const closeComboModal = useCallback(() => setComboModalOpen(false), []);
  const applyComboFilter = useCallback((comboId: string) => {
    if (!bandType) return;
    const params = new URLSearchParams(searchParams);
    params.set('combo', comboId);
    params.set('page', '1');
    scrollContainerRef.current?.scrollTo(0, 0);
    setSearchParams(params, { replace: true });
    setComboModalOpen(false);
  }, [bandType, scrollContainerRef, searchParams, setSearchParams]);
  const clearComboFilter = useCallback(() => {
    if (!bandType) return;
    const params = new URLSearchParams(searchParams);
    if (appliedBandComboFilter?.bandType === bandType) params.set('combo', PAGE_BAND_COMBO_ALL_VALUE);
    else params.delete('combo');
    params.set('page', '1');
    scrollContainerRef.current?.scrollTo(0, 0);
    setSearchParams(params, { replace: true });
    setComboModalOpen(false);
  }, [appliedBandComboFilter?.bandType, bandType, scrollContainerRef, searchParams, setSearchParams]);

  useEffect(() => {
    registerLeaderboardActions({
      openMetric: experimentalRanksEnabled ? openMetricModal : undefined,
      openBandCombo: openComboModal,
      bandComboActive: !!activeComboId,
      bandComboInstruments: activeComboInstruments,
      bandComboLabel: comboFilterLabel,
    });
    return () => registerLeaderboardActions(null);
  }, [activeComboId, activeComboInstruments, comboFilterLabel, experimentalRanksEnabled, openComboModal, openMetricModal, registerLeaderboardActions]);

  const leaderboardQuery = useQuery({
    queryKey: queryKeys.bandRankings(bandType ?? 'unknown', activeComboId, metric, pageParam, LEADERBOARD_PAGE_SIZE, selectedPlayerAccountId, selectedBandTeamKey),
    queryFn: () => api.getBandRankings(bandType!, activeComboId, metric, pageParam, LEADERBOARD_PAGE_SIZE, selectedPlayerAccountId, selectedBandTeamKey),
    enabled: !!bandType,
    placeholderData: (previous) => previous,
  });

  const data = leaderboardQuery.data;
  const entries = data?.entries ?? [];
  const selectedBandEntry = data?.selectedBandEntry ?? null;
  const selectedPlayerEntry = data?.selectedPlayerEntry ?? null;
  const selectedFooterEntry = selectedBandEntry ?? selectedPlayerEntry;
  const hasSelectedFooter = !!selectedFooterEntry;
  const totalTeams = data?.totalTeams;
  const selectedBandFooterName = useMemo(() => {
    if (!selectedFooterEntry) return undefined;
    return formatBandTeamName(selectedFooterEntry.teamMembers, profile?.type === 'band' ? profile.displayName : selectedFooterEntry.teamKey);
  }, [profile, selectedFooterEntry]);
  const selectedBandFooterRoute = useMemo(() => {
    if (!selectedFooterEntry || !selectedBandFooterName || !bandType) return undefined;
    return getBandProfileRoute(selectedFooterEntry.bandId, {
      bandType,
      teamKey: selectedFooterEntry.teamKey,
      names: selectedBandFooterName,
      ...(selectedBandEntry ? {} : { accountId: selectedPlayerAccountId }),
    }, profile);
  }, [bandType, profile, selectedBandEntry, selectedBandFooterName, selectedFooterEntry, selectedPlayerAccountId]);
  const selectedBandFooterRankWidth = useMemo(() => {
    if (!selectedFooterEntry) return undefined;
    return computeRankWidth([getBandRankForMetric(selectedFooterEntry, metric)]);
  }, [metric, selectedFooterEntry]);
  const selectedBandUsesPercentile = usesPercentileValueDisplay(metric);
  const activeFilterInstruments = activeComboInstruments.length > 0
    ? activeComboInstruments
    : undefined;
  const activeFilterSource = appliedBandComboFilter && appliedBandComboFilter.bandType === bandType && appliedBandComboFilter.comboId === activeComboId
    ? appliedBandComboFilter
    : null;
  const activeFilterConfigurations = activeFilterSource
    ? activeFilterSource.configurations
    : undefined;
  const totalPages = data ? Math.max(1, Math.ceil(data.totalTeams / LEADERBOARD_PAGE_SIZE)) : 1;
  const loading = leaderboardQuery.isFetching && !data;
  const hasPagination = !!data && !leaderboardQuery.error && totalPages > 1;

  const goToPage = useCallback((nextPage: number) => {
    if (!bandType || nextPage < 1 || (totalPages && nextPage > totalPages)) return;
    scrollContainerRef.current?.scrollTo(0, 0);
    const params = new URLSearchParams(searchParams);
    params.set('rankBy', metric);
    params.set('page', String(nextPage));
    setSearchParams(params, { replace: true });
  }, [bandType, metric, scrollContainerRef, searchParams, setSearchParams, totalPages]);

  useEffect(() => {
    if (data && pageParam > totalPages) goToPage(totalPages);
  }, [data, goToPage, pageParam, totalPages]);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'ArrowLeft' && pageParam > 1) goToPage(pageParam - 1);
      if (event.key === 'ArrowRight' && totalPages && pageParam < totalPages) goToPage(pageParam + 1);
    }

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [goToPage, pageParam, totalPages]);

  const { phase, shouldStagger } = usePageTransition(`bandRankings:${bandType}:${activeComboId ?? 'all'}:${metric}:${selectedBandTeamKey ?? selectedPlayerAccountId ?? 'none'}:${pageParam}`, !loading);
  const { forIndex: stagger, clearAnim } = useStagger(shouldStagger);
  const selectedFooterPlacement = isMobileChrome ? 'aboveFab' : 'default';
  useLeaderboardFooterScrollMargin({ hasFab, hasPagination, hasPlayerFooter: hasSelectedFooter, footerPlacement: selectedFooterPlacement });
  const styles = useStyles();

  if (!bandType) {
    return <PageMessage>{t('band.notFound')}</PageMessage>;
  }

  const bandLabel = bandTypeLabel(bandType, t);
  const showMobilePageHeader = !isMobileChrome || settings.showButtonsInHeaderMobile;
  const comboFilterPill = (
    <BandFilterPill
      label={comboFilterLabel}
      selectedInstruments={activeComboInstruments}
      bandType={activeComboId ? bandType : null}
      onClick={openComboModal}
    />
  );
  return (
    <Page
      scrollRef={scrollRef}
      staggerRushRef={staggerRushRef}
      scrollRestoreKey={`bandRankings:${bandType}:${activeComboId ?? 'all'}:${metric}:${pageParam}`}
      scrollDeps={[phase, entries.length, hasSelectedFooter, pageParam, metric, bandType, activeComboId, selectedBandTeamKey, selectedPlayerAccountId]}
      loadPhase={phase}
      fabSpacer="none"
      before={isMobileChrome ? (
        <PageHeaderTransition visible={showMobilePageHeader}>
          <PageHeader title={renderPageTitle(bandLabel, t('rankings.title'), totalTeams, t)} />
        </PageHeaderTransition>
      ) : showMobilePageHeader ? (
        <PageHeader
          title={renderPageTitle(bandLabel, t('rankings.title'), totalTeams, t)}
          actions={(
            <>
              {comboFilterPill}
              {experimentalRanksEnabled && (
                <ActionPill
                  icon={<IoOptions size={Size.iconAction} />}
                  label={t(`rankings.metric.${metric}`)}
                  onClick={openMetricModal}
                  active={metric !== 'totalscore'}
                />
              )}
            </>
          )}
        />
      ) : undefined}
      after={<>
        <RankByModal
          visible={metricModal.visible}
          draft={metricModal.draft}
          onDraftChange={(nextMetric) => metricModal.setDraft(coerceBandRankingMetric(nextMetric, experimentalRanksEnabled))}
          onClose={metricModal.close}
          onApply={applyMetric}
          onReset={metricModal.reset}
          experimentalRanksEnabled={experimentalRanksEnabled}
          metrics={getEnabledBandRankingMetrics(experimentalRanksEnabled)}
          subject="bands"
        />
        <BandComboFilterModal
          visible={comboModalOpen}
          bandType={bandType}
          activeInstruments={activeComboInstruments}
          selectedBandName={selectedBandMatchesView ? selectedBand?.displayName : undefined}
          selectedBandHasGlobalFilter={selectedBandMatchesView && selectedBandHasGlobalFilter}
          onCancel={closeComboModal}
          onApplyCombo={applyComboFilter}
          onClearCombo={clearComboFilter}
        />
      </>}
    >
      {phase === LoadPhase.ContentIn && leaderboardQuery.error && (() => {
        const parsed = parseApiError(String(leaderboardQuery.error));
        return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={stagger(0)} onAnimationEnd={clearAnim} />;
      })()}

      {phase === LoadPhase.ContentIn && !leaderboardQuery.error && data && (
        <div style={styles.content}>
          {entries.length === 0 ? (
            <EmptyState title={t('rankings.noBandRankings')} style={stagger(0)} onAnimationEnd={clearAnim} />
          ) : (
            <div data-testid="band-rankings-card-list" style={styles.cardGrid}>
              {entries.map((entry, index) => (
                <BandRankingPlayerCard
                  key={entry.bandId || entry.teamKey}
                  entry={entry}
                  bandType={bandType}
                  metric={metric}
                  totalTeams={totalTeams}
                  activeFilterComboId={activeComboId}
                  activeFilterTeamKey={activeFilterSource?.teamKey}
                  activeFilterInstruments={activeFilterInstruments}
                  activeFilterConfigurations={activeFilterConfigurations}
                  testId={`band-rankings-entry-${index}`}
                  style={stagger(index)}
                  onAnimationEnd={clearAnim}
                />
              ))}
            </div>
          )}

          {hasPagination && (
            <FixedLeaderboardPagination
              page={pageParam}
              totalPages={totalPages}
              onGoToPage={goToPage}
              isMobile={isMobile}
              hasFab={hasFab}
              hasPlayerFooter={hasSelectedFooter}
              footerPlacement={selectedFooterPlacement}
            />
          )}
          {hasSelectedFooter && selectedFooterEntry && selectedBandFooterName && selectedBandFooterRoute && (
            <FixedLeaderboardPlayerFooter
              hasFab={hasFab}
              reserveFabSpace={selectedFooterPlacement === 'aboveFab' ? false : hasFab}
              footerPlacement={selectedFooterPlacement}
            >
              {({ className, style }) => (
                <Link to={selectedBandFooterRoute} className={className} style={style}>
                  <RankingEntry
                    rank={getBandRankForMetric(selectedFooterEntry, metric)}
                    displayName={selectedBandFooterName}
                    ratingLabel={formatRating(getBandRatingForMetric(selectedFooterEntry, metric), metric)}
                    songsLabel={getBandSongsLabel(selectedFooterEntry, metric)}
                    percentileValueDisplay={selectedBandUsesPercentile ? formatRankingValueDisplay(getBandRatingForMetric(selectedFooterEntry, metric), metric) : undefined}
                    bayesianRankDisplay={selectedBandUsesPercentile ? formatBayesianRatingDisplay(getBandBayesianRatingForMetric(selectedFooterEntry, metric), metric) : undefined}
                    bayesianRankColor={selectedBandUsesPercentile ? rankColor(getBandRankForMetric(selectedFooterEntry, metric), totalTeams ?? 0) : undefined}
                    ratingPillTier={getRatingPillTier(getBandRatingForMetric(selectedFooterEntry, metric), metric)}
                    songsLabelPrimary={metric === 'fcrate'}
                    songsLabelGoldPrefix={metric === 'fcrate'}
                    isPlayer
                    rankWidth={selectedBandFooterRankWidth}
                    reserveTenDigitScoreWidth={metric === 'totalscore' && !(isMobile && hasFab)}
                  />
                </Link>
              )}
            </FixedLeaderboardPlayerFooter>
          )}
        </div>
      )}
    </Page>
  );
}

function renderPageTitle(
  bandLabel: string,
  rankingsTitle: string,
  totalTeams: number | undefined,
  t: ReturnType<typeof useTranslation>['t'],
) {
  return (
    <div style={titleStyles.wrapper}>
      <span style={titleStyles.title}>{`${bandLabel} ${rankingsTitle}`}</span>
      {totalTeams != null && (
        <span style={titleStyles.subtitle}>{t('rankings.totalRankedBands', { count: totalTeams, formattedCount: totalTeams.toLocaleString() })}</span>
      )}
    </div>
  );
}

function useStyles() {
  return useMemo(() => ({
    content: {
      ...flexColumn,
      gap: Gap.section,
    } as CSSProperties,
    cardGrid: {
      display: Display.flex,
      flexDirection: 'column',
      gap: Gap.md,
    } as CSSProperties,
  }), []);
}

const titleStyles = {
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