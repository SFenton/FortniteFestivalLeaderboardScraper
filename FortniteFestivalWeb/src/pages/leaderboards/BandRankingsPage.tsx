/* eslint-disable react/forbid-dom-props -- page-level dynamic styles use inline style objects */
import { useCallback, useEffect, useMemo, useRef, type CSSProperties } from 'react';
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
import { FixedLeaderboardPagination, FixedLeaderboardPlayerFooter, useLeaderboardFooterScrollMargin } from '../../components/leaderboard/LeaderboardPaginationFooter';
import Page from '../Page';
import { PageMessage } from '../PageMessage';
import RankByModal from './modals/RankByModal';
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
import { computeRankWidth, formatBayesianRatingDisplay, formatRankingValueDisplay, formatRating, getRatingPillTier, LEADERBOARD_PAGE_SIZE, usesPercentileValueDisplay } from './helpers/rankingHelpers';
import { coerceBandRankingMetric, formatBandTeamName, getBandBayesianRatingForMetric, getBandRankForMetric, getBandRatingForMetric, getBandSongsLabel, getEnabledBandRankingMetrics } from './helpers/bandRankingHelpers';
import BandRankingPlayerCard from './components/BandRankingPlayerCard';
import { RankingEntry } from './components/RankingEntry';
import { Routes } from '../../routes';

export default function BandRankingsPage() {
  const { t } = useTranslation();
  const { bandType: rawBandType } = useParams<{ bandType: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const { settings } = useSettings();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const hasFab = isMobileChrome;
  const fabSearch = useFabSearch();
  const scrollContainerRef = useScrollContainer();
  const bandType = coerceBandType(rawBandType);
  const { profile } = useSelectedProfile();
  const appliedBandComboFilter = useAppliedBandComboFilter();
  const activeComboId = appliedBandComboFilter && appliedBandComboFilter.bandType === bandType ? appliedBandComboFilter.comboId : undefined;
  const selectedPlayerAccountId = profile?.type === 'player' ? profile.accountId : undefined;
  const selectedBandTeamKey = profile?.type === 'band' && profile.bandType === bandType ? profile.teamKey : undefined;
  const rawMetric = searchParams.get('rankBy') ?? loadLeaderboardRankBy();
  const metric = coerceBandRankingMetric(rawMetric, true);
  const pageParam = Math.max(1, Number(searchParams.get('page')) || 1);
  const metricModal = useModalState<BandRankingMetric>(() => 'totalscore');
  const scrollRef = useRef<HTMLDivElement>(null);
  const staggerRushRef = useRef<(() => void) | undefined>(undefined);

  const openMetricModal = useCallback(() => {
    metricModal.open(metric);
  }, [metric, metricModal]);

  const applyMetric = useCallback(() => {
    if (!bandType) return;
    const nextMetric = coerceBandRankingMetric(metricModal.draft, true);
    metricModal.close();
    saveLeaderboardRankBy(nextMetric);
    scrollContainerRef.current?.scrollTo(0, 0);
    setSearchParams({ rankBy: nextMetric, page: '1' }, { replace: true });
  }, [bandType, metricModal, scrollContainerRef, setSearchParams]);

  useEffect(() => {
    fabSearch.registerLeaderboardActions({ openMetric: openMetricModal, openInstrument: () => {} });
    return () => fabSearch.registerLeaderboardActions({ openMetric: () => {}, openInstrument: () => {} });
  }, [fabSearch, openMetricModal]);

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
    if (!selectedFooterEntry || !selectedBandFooterName) return undefined;
    return Routes.band(selectedFooterEntry.bandId, {
      bandType,
      teamKey: selectedFooterEntry.teamKey,
      names: selectedBandFooterName,
      ...(selectedBandEntry ? {} : { accountId: selectedPlayerAccountId }),
    });
  }, [bandType, selectedBandEntry, selectedBandFooterName, selectedFooterEntry, selectedPlayerAccountId]);
  const selectedBandFooterRankWidth = useMemo(() => {
    if (!selectedFooterEntry) return undefined;
    return computeRankWidth([getBandRankForMetric(selectedFooterEntry, metric)]);
  }, [metric, selectedFooterEntry]);
  const selectedBandUsesPercentile = usesPercentileValueDisplay(metric);
  const activeFilterInstruments = bandType === 'Band_Duets' && appliedBandComboFilter && appliedBandComboFilter.bandType === bandType
    ? appliedBandComboFilter.assignments.map(assignment => assignment.instrument)
    : undefined;
  const activeFilterConfigurations = bandType === 'Band_Duets' && appliedBandComboFilter && appliedBandComboFilter.bandType === bandType
    ? appliedBandComboFilter.configurations
    : undefined;
  const totalPages = data ? Math.max(1, Math.ceil(data.totalTeams / LEADERBOARD_PAGE_SIZE)) : 1;
  const loading = leaderboardQuery.isFetching && !data;
  const hasPagination = !!data && !leaderboardQuery.error && totalPages > 1;

  const goToPage = useCallback((nextPage: number) => {
    if (!bandType || nextPage < 1 || (totalPages && nextPage > totalPages)) return;
    scrollContainerRef.current?.scrollTo(0, 0);
    setSearchParams({ rankBy: metric, page: String(nextPage) }, { replace: true });
  }, [bandType, metric, scrollContainerRef, setSearchParams, totalPages]);

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
  useLeaderboardFooterScrollMargin({ hasFab, hasPagination, hasPlayerFooter: hasSelectedFooter });
  const styles = useStyles();

  if (!bandType) {
    return <PageMessage>{t('band.notFound')}</PageMessage>;
  }

  const bandLabel = bandTypeLabel(bandType, t);
  const showMobilePageHeader = !isMobileChrome || settings.showButtonsInHeaderMobile;

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
            <ActionPill
              icon={<IoOptions size={Size.iconAction} />}
              label={t(`rankings.metric.${metric}`)}
              onClick={openMetricModal}
              active={metric !== 'totalscore'}
            />
          )}
        />
      ) : undefined}
      after={<RankByModal
        visible={metricModal.visible}
        draft={metricModal.draft}
        onDraftChange={(nextMetric) => metricModal.setDraft(coerceBandRankingMetric(nextMetric, true))}
        onClose={metricModal.close}
        onApply={applyMetric}
        onReset={metricModal.reset}
        experimentalRanksEnabled={true}
        metrics={getEnabledBandRankingMetrics(true)}
        subject="bands"
      />}
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
                  activeFilterTeamKey={appliedBandComboFilter?.teamKey}
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
            />
          )}
          {hasSelectedFooter && selectedFooterEntry && selectedBandFooterName && selectedBandFooterRoute && (
            <FixedLeaderboardPlayerFooter hasFab={hasFab}>
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