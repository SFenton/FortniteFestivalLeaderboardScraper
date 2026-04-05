/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useState, useEffect, useCallback, useMemo, useRef, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useQueries } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { IoOptions } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { ActionPill } from '../../components/common/ActionPill';
import RankingCard from './components/RankingCard';
import EmptyState from '../../components/common/EmptyState';
import { parseApiError } from '../../utils/apiError';
import { buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import type { RankingMetric, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { LoadPhase } from '@festival/core';
import RankByModal from './modals/RankByModal';
import RankHistoryChart from './components/RankHistoryChart';
import { useRankHistoryAll } from '../../hooks/chart/useRankHistory';
import { loadLeaderboardRankBy, saveLeaderboardRankBy } from '../../utils/leaderboardSettings';
import { useModalState } from '../../hooks/ui/useModalState';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useGridColumnCount } from '../../hooks/ui/useGridColumnCount';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';

import {
  Display, Overflow, Gap,
  GridTemplate, Size, STAGGER_INTERVAL, FADE_DURATION,
} from '@festival/theme';
import { leaderboardsSlides } from './firstRun';

/** Set to 1 to stagger the right column one slot (125 ms) after the left. */
const COLUMN_STAGGER_OFFSET = 1;

export default function LeaderboardsOverviewPage() {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const { player } = useTrackedPlayer();
  const isMobile = useIsMobileChrome();
  const fabSearch = useFabSearch();
  const scrollContainerRef = useScrollContainer();
  const [searchParams, setSearchParams] = useSearchParams();
  const rawMetric = (searchParams.get('rankBy') ?? loadLeaderboardRankBy()) as RankingMetric;
  const metric = settings.enableExperimentalRanks ? rawMetric : 'totalscore' as RankingMetric;

  const metricModal = useModalState<RankingMetric>(() => 'totalscore');

  const openMetricModal = useCallback(() => {
    metricModal.open(metric);
  }, [metricModal, metric]);

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
    fabSearch.registerLeaderboardActions({ openMetric: openMetricModal, openInstrument: () => {} });
    return () => fabSearch.registerLeaderboardActions({ openMetric: () => {}, openInstrument: () => {} });
  }, [fabSearch, openMetricModal]);

  const instruments = useMemo(() => visibleInstruments(settings), [settings]);

  // Fetch top-10 per visible instrument
  const rankingQueries = useQueries({
    queries: instruments.map((inst) => ({
      queryKey: queryKeys.rankings(inst, metric, 1, 10),
      queryFn: () => api.getRankings(inst, metric, 1, 10),
    })),
  });

  // Fetch player ranking per visible instrument (only when player is tracked)
  const playerQueries = useQueries({
    queries: player
      ? instruments.map((inst) => ({
          queryKey: queryKeys.playerRanking(inst, player.accountId),
          queryFn: () => api.getPlayerRanking(inst, player.accountId),
        }))
      : [],
  });

  // Hoist rank-history loading so the page waits for graph data before staggering
  const allHistory = useRankHistoryAll(instruments, player?.accountId, metric);
  const historyLoading = player ? instruments.some(inst => allHistory[inst]?.loading) : false;
  const historyAllCached = !player || instruments.every(inst => allHistory[inst]?.chartData != null && !allHistory[inst]?.loading);

  const isLoading = rankingQueries.some(q => q.isLoading) || historyLoading;
  const hasCachedData = rankingQueries.every(q => q.data != null) && historyAllCached;
  const allErrored = !isLoading && rankingQueries.length > 0 && rankingQueries.every(q => q.error);
  const loadPhase = isLoading ? LoadPhase.Loading : LoadPhase.ContentIn;

  const [shouldStagger, setShouldStagger] = useState(!hasCachedData);
  const maxEntriesPerCard = useMemo(
    () => Math.max(0, ...rankingQueries.map(q => q.data?.entries?.length ?? 0)),
    [rankingQueries],
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
    const totalRows = Math.ceil(instruments.length / cols);
    const chartSlot = player ? 1 : 0;
    const totalAnimTime =
      (chartSlot + totalRows * itemsPerCard + (cols - 1) * COLUMN_STAGGER_OFFSET) * STAGGER_INTERVAL + FADE_DURATION;
    const id = setTimeout(() => setShouldStagger(false), totalAnimTime);
    return () => clearTimeout(id);
  }, [loadPhase, shouldStagger, maxEntriesPerCard, cols, instruments.length, itemsPerCard]);

  const s = useLeaderboardsStyles();
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: !!player, experimentalRanksEnabled: settings.enableExperimentalRanks }), [player, settings.enableExperimentalRanks]);

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
            !isMobile && !allErrored && settings.enableExperimentalRanks ? (
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
          visible={metricModal.visible}
          draft={metricModal.draft}
          onDraftChange={metricModal.setDraft}
          onClose={metricModal.close}
          onApply={applyMetric}
          onReset={metricModal.reset}
        />
      }
    >
      {loadPhase === LoadPhase.ContentIn && allErrored && (() => {
        const parsed = parseApiError(String(rankingQueries[0]!.error));
        return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
      })()}
      {loadPhase === LoadPhase.ContentIn && !allErrored && player && (
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
        <div ref={gridRef} style={s.grid}>
          {instruments.map((inst, idx) => {
            const q = rankingQueries[idx];
            const pq = player ? playerQueries[idx] : undefined;
            const gridRow = Math.floor(idx / cols);
            const gridCol = idx % cols;
            const chartSlots = player ? 1 : 0;
            const offset = chartSlots + gridRow * itemsPerCard + gridCol * COLUMN_STAGGER_OFFSET;
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
      )}
    </Page>
  );
}

function useLeaderboardsStyles() {
  return useMemo(() => ({
    grid: {
      display: Display.grid,
      gridTemplateColumns: GridTemplate.autoFillInstrument,
      gap: `${Gap.section}px ${Gap.md}px`,
      overflow: Overflow.hidden,
    } as CSSProperties,
    chartWrapper: {
      marginBottom: Gap.section,
    } as CSSProperties,
  }), []);
}
