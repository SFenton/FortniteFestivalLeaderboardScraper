/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useState, useEffect, useCallback, useMemo, useRef, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useQueries, useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { IoOptions } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useFeatureFlags } from '../../contexts/FeatureFlagsContext';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { ActionPill } from '../../components/common/ActionPill';
import RankingCard from './components/RankingCard';
import BandRankingCard from './components/BandRankingCard';
import EmptyState from '../../components/common/EmptyState';
import { parseApiError } from '../../utils/apiError';
import { buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import type { RankingMetric, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { LoadPhase } from '@festival/core';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import RankByModal from './modals/RankByModal';
import RankHistoryChart from './components/RankHistoryChart';
import { useRankHistoryAll } from '../../hooks/chart/useRankHistory';
import { loadLeaderboardRankBy, saveLeaderboardRankBy } from '../../utils/leaderboardSettings';
import { useModalState } from '../../hooks/ui/useModalState';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useGridColumnCount } from '../../hooks/ui/useGridColumnCount';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useAppliedBandComboFilter } from '../../contexts/BandFilterActionContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';

import {
  Display, Overflow, Gap,
  GridTemplate, Size, STAGGER_INTERVAL, FADE_DURATION, flexColumn,
} from '@festival/theme';
import { leaderboardsSlides } from './firstRun';
import { coerceRankingMetric } from './helpers/rankingHelpers';
import { coerceBandRankingMetric } from './helpers/bandRankingHelpers';
import { BAND_TYPES } from '../../utils/bandTypes';

/** Set to 1 to stagger the right column one slot (125 ms) after the left. */
const COLUMN_STAGGER_OFFSET = 1;

export default function LeaderboardsOverviewPage() {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const { experimentalRanks: experimentalRanksEnabled = false, leaderboards: rankHistoryEnabled = false, playerBands: playerBandsEnabled = false } = useFeatureFlags();
  const { profile, player } = useTrackedPlayer();
  const selectedAccountId = player?.accountId;
  const selectedBandTeamKey = profile?.type === 'band' ? profile.teamKey : undefined;
  const selectedBandType = profile?.type === 'band' ? profile.bandType : undefined;
  const appliedBandComboFilter = useAppliedBandComboFilter();
  const isMobile = useIsMobileChrome();
  const fabSearch = useFabSearch();
  const scrollContainerRef = useScrollContainer();
  const [searchParams, setSearchParams] = useSearchParams();
  const rawMetric = (searchParams.get('rankBy') ?? loadLeaderboardRankBy()) as RankingMetric;
  const metric = coerceRankingMetric(rawMetric, experimentalRanksEnabled);
  const bandMetric = coerceBandRankingMetric(metric, experimentalRanksEnabled);

  const metricModal = useModalState<RankingMetric>(() => 'totalscore');

  const openMetricModal = useCallback(() => {
    if (!experimentalRanksEnabled) return;
    metricModal.open(metric);
  }, [experimentalRanksEnabled, metricModal, metric]);

  const staggerRushRef = useRef<(() => void) | undefined>(undefined);
  const resetRush = useCallback(() => staggerRushRef.current?.(), []);

  const applyMetric = useCallback(() => {
    scrollContainerRef.current?.scrollTo(0, 0);
    resetRush();
    setShouldStagger(true);
    saveLeaderboardRankBy(metricModal.draft);
    setSearchParams({ rankBy: metricModal.draft }, { replace: true });
    metricModal.close();
  }, [metricModal, setSearchParams, scrollContainerRef, resetRush]);

  useEffect(() => {
    fabSearch.registerLeaderboardActions({ openMetric: experimentalRanksEnabled ? openMetricModal : () => {}, openInstrument: () => {} });
    return () => fabSearch.registerLeaderboardActions({ openMetric: () => {}, openInstrument: () => {} });
  }, [experimentalRanksEnabled, fabSearch, openMetricModal]);

  const instruments = useMemo(() => visibleInstruments(settings), [settings]);

  // Fetch top-10 per visible instrument
  const rankingQueries = useQueries({
    queries: instruments.map((inst) => ({
      queryKey: queryKeys.rankings(inst, metric, 1, 10),
      queryFn: () => api.getRankings(inst, metric, 1, 10),
    })),
  });

  const bandTypes = useMemo(() => playerBandsEnabled ? BAND_TYPES : [], [playerBandsEnabled]);

  const bandRankingQueries = useQueries({
    queries: bandTypes.map((bandType) => {
      const selectedTeamKey = selectedBandType === bandType ? selectedBandTeamKey : undefined;
      const comboId = appliedBandComboFilter && appliedBandComboFilter.bandType === bandType ? appliedBandComboFilter.comboId : undefined;
      return {
        queryKey: queryKeys.bandRankings(bandType, comboId, bandMetric, 1, 10, selectedAccountId, selectedTeamKey),
        queryFn: () => api.getBandRankings(bandType, comboId, bandMetric, 1, 10, selectedAccountId, selectedTeamKey),
      };
    }),
  });

  const selectedBandComboId = selectedBandType && appliedBandComboFilter && appliedBandComboFilter.bandType === selectedBandType
    ? appliedBandComboFilter.comboId
    : undefined;

  const selectedBandRankingQuery = useQuery({
    queryKey: selectedBandType && selectedBandTeamKey
      ? queryKeys.bandRanking(selectedBandType, selectedBandTeamKey, selectedBandComboId, bandMetric)
      : ['bandRanking', 'none'],
    queryFn: () => api.getBandRanking(selectedBandType!, selectedBandTeamKey!, selectedBandComboId, bandMetric),
    enabled: playerBandsEnabled && !!selectedBandType && !!selectedBandTeamKey,
    retry: false,
  });

  // Fetch player ranking per visible instrument (only when player is tracked)
  const playerQueries = useQueries({
    queries: player
      ? instruments.map((inst) => ({
          queryKey: queryKeys.playerRanking(inst, player.accountId, metric),
          queryFn: () => api.getPlayerRanking(inst, player.accountId, metric),
        }))
      : [],
  });

  // Hoist rank-history loading so the page waits for graph data before staggering.
  // When the rank-history flag is off, pass [] so no queries are issued.
  const allHistory = useRankHistoryAll(rankHistoryEnabled ? instruments : [], player?.accountId, metric);
  const historyLoading = rankHistoryEnabled && player ? instruments.some(inst => allHistory[inst]?.loading) : false;
  const historyAllCached = !rankHistoryEnabled || !player || instruments.every(inst => allHistory[inst]?.chartData != null && !allHistory[inst]?.loading);

  const leaderboardQueries = useMemo(() => [...rankingQueries, ...bandRankingQueries], [rankingQueries, bandRankingQueries]);
  const isLoading = leaderboardQueries.some(query => query.isLoading) || historyLoading;
  const hasCachedData = leaderboardQueries.every(query => query.data != null) && historyAllCached;
  const allErrored = !isLoading && leaderboardQueries.length > 0 && leaderboardQueries.every(query => query.error);
  const { phase: loadPhase } = useLoadPhase(!isLoading, { skipAnimation: hasCachedData });

  const [shouldStagger, setShouldStagger] = useState(!hasCachedData);
  const maxEntriesPerCard = useMemo(
    () => Math.max(0, ...leaderboardQueries.map(query => query.data?.entries?.length ?? 0)),
    [leaderboardQueries],
  );
  const scrollRef = useRef<HTMLDivElement>(null);
  const [cols, gridRef] = useGridColumnCount();
  const itemsPerCard = maxEntriesPerCard + (player ? 3 : 2); // header + entries + (player footer?) + button

  const totalAccountsByInstrument = useMemo(
    () => Object.fromEntries(instruments.map((inst, i) => [inst, rankingQueries[i]?.data?.totalAccounts ?? 0])),
    [instruments, rankingQueries],
  );

  useEffect(() => {
    if (loadPhase !== LoadPhase.ContentIn || !shouldStagger) return;
    const instrumentRows = Math.ceil(instruments.length / cols);
    const totalRows = instrumentRows + bandTypes.length;
    const chartSlot = player && rankHistoryEnabled ? 1 : 0;
    const totalAnimTime =
      (chartSlot + totalRows * itemsPerCard + (cols - 1) * COLUMN_STAGGER_OFFSET) * STAGGER_INTERVAL + FADE_DURATION;
    const id = setTimeout(() => setShouldStagger(false), totalAnimTime);
    return () => clearTimeout(id);
  }, [bandTypes.length, cols, instruments.length, itemsPerCard, loadPhase, player, rankHistoryEnabled, shouldStagger]);

  const s = useLeaderboardsStyles();
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: !!player, experimentalRanksEnabled }), [experimentalRanksEnabled, player]);
  const firstLeaderboardError = leaderboardQueries.find(query => query.error)?.error;
  const chartSlots = player && rankHistoryEnabled ? 1 : 0;
  const instrumentRows = Math.ceil(instruments.length / cols);
  const getInstrumentCardStaggerOffset = useCallback((cardIndex: number) => {
    const gridRow = Math.floor(cardIndex / cols);
    const gridCol = cardIndex % cols;
    return chartSlots + gridRow * itemsPerCard + gridCol * COLUMN_STAGGER_OFFSET;
  }, [chartSlots, cols, itemsPerCard]);
  const getBandCardStaggerOffset = useCallback((bandIndex: number) => (
    chartSlots + instrumentRows * itemsPerCard + bandIndex * itemsPerCard
  ), [chartSlots, instrumentRows, itemsPerCard]);

  return (
    <Page
      scrollRef={scrollRef}
      staggerRushRef={staggerRushRef}
      scrollRestoreKey="leaderboards"
      loadPhase={loadPhase}
      fabSpacer={loadPhase === LoadPhase.ContentIn && allErrored ? 'none' : 'end'}
      firstRun={{ key: 'leaderboards', label: t('nav.leaderboards'), slides: leaderboardsSlides, gateContext: firstRunGateCtx }}
      before={
        isMobile ? undefined : (
        <PageHeader
          title={t('rankings.title')}
          actions={
            !isMobile && !allErrored && experimentalRanksEnabled ? (
              <ActionPill
                icon={<IoOptions size={Size.iconAction} />}
                label={t(`rankings.metric.${metric}`)}
                onClick={openMetricModal}
                active={metric !== 'totalscore'}
              />
            ) : undefined
          }
        />
        )
      }
      after={
        <RankByModal
          visible={metricModal.visible && experimentalRanksEnabled}
          draft={metricModal.draft}
          onDraftChange={metricModal.setDraft}
          onClose={metricModal.close}
          onApply={applyMetric}
          onReset={metricModal.reset}
          experimentalRanksEnabled={experimentalRanksEnabled}
        />
      }
    >
      {loadPhase === LoadPhase.ContentIn && allErrored && (() => {
        const parsed = parseApiError(String(firstLeaderboardError));
        return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
      })()}
      {loadPhase === LoadPhase.ContentIn && !allErrored && player && rankHistoryEnabled && (
        <div style={{ ...s.chartWrapper, ...buildStaggerStyle(shouldStagger ? 0 : null) }} onAnimationEnd={clearStaggerStyle}>
          <RankHistoryChart
            accountId={player.accountId}
            instruments={instruments}
            metric={metric}
            totalAccountsByInstrument={totalAccountsByInstrument}
            skipAnimation={!shouldStagger}
          />
        </div>
      )}
      {loadPhase === LoadPhase.ContentIn && !allErrored && (
        <div style={s.contentStack}>
          <div ref={gridRef} data-testid="leaderboards-instrument-grid" style={s.grid}>
            {instruments.map((inst, idx) => {
              const q = rankingQueries[idx];
              const pq = player ? playerQueries[idx] : undefined;
              const offset = getInstrumentCardStaggerOffset(idx);
              return (
                <RankingCard
                  key={inst}
                  instrument={inst as InstrumentKey}
                  metric={metric}
                  entries={q?.data?.entries ?? []}
                  totalAccounts={q?.data?.totalAccounts ?? 0}
                  playerRanking={pq?.data ?? null}
                  playerAccountId={player?.accountId}
                  error={q?.error ? String(q.error) : null}
                  shouldStagger={shouldStagger}
                  staggerOffset={offset}
                />
              );
            })}
          </div>
          {bandTypes.length > 0 && (
            <div data-testid="leaderboards-band-section-stack" style={s.bandSectionStack}>
              {bandTypes.map((bandType, idx) => {
                const bandQuery = bandRankingQueries[idx];
                const activeFilterInstruments = appliedBandComboFilter && appliedBandComboFilter.bandType === bandType
                  ? appliedBandComboFilter.assignments.map(assignment => assignment.instrument)
                  : undefined;
                const activeFilterComboId = appliedBandComboFilter && appliedBandComboFilter.bandType === bandType
                  ? appliedBandComboFilter.comboId
                  : undefined;
                const activeFilterTeamKey = appliedBandComboFilter && appliedBandComboFilter.bandType === bandType
                  ? appliedBandComboFilter.teamKey
                  : undefined;
                const activeFilterConfigurations = appliedBandComboFilter && appliedBandComboFilter.bandType === bandType
                  ? appliedBandComboFilter.configurations
                  : undefined;
                return (
                  <BandRankingCard
                    key={bandType}
                    bandType={bandType}
                    metric={bandMetric}
                    entries={bandQuery?.data?.entries ?? []}
                    selectedPlayerEntry={bandQuery?.data?.selectedPlayerEntry ?? null}
                    selectedBandEntry={bandQuery?.data?.selectedBandEntry ?? (selectedBandType === bandType ? selectedBandRankingQuery.data ?? null : null)}
                    selectedAccountId={selectedAccountId}
                    activeFilterComboId={activeFilterComboId}
                    activeFilterTeamKey={activeFilterTeamKey}
                    activeFilterInstruments={activeFilterInstruments}
                    activeFilterConfigurations={activeFilterConfigurations}
                    totalTeams={bandQuery?.data?.totalTeams ?? 0}
                    error={bandQuery?.error ? String(bandQuery.error) : null}
                    shouldStagger={shouldStagger}
                    staggerOffset={getBandCardStaggerOffset(idx)}
                  />
                );
              })}
            </div>
          )}
        </div>
      )}
    </Page>
  );
}

function useLeaderboardsStyles() {
  return useMemo(() => ({
    contentStack: {
      ...flexColumn,
      gap: Gap.section,
    } as CSSProperties,
    grid: {
      display: Display.grid,
      gridTemplateColumns: GridTemplate.autoFillInstrument,
      gap: `${Gap.section}px ${Gap.md}px`,
      overflow: Overflow.hidden,
    } as CSSProperties,
    bandSectionStack: {
      ...flexColumn,
      gap: Gap.section,
      width: '100%',
    } as CSSProperties,
    chartWrapper: {
      marginBottom: Gap.section,
    } as CSSProperties,
  }), []);
}
