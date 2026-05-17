/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useState, useEffect, useCallback, useMemo, useRef, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useQueries, useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { IoOptions, IoPeople, IoStatsChart } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
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
import { serverInstrumentLabel, type AccountRankingDto, type BandType, type RankingMetric, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { LoadPhase } from '@festival/core';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import RankByModal from './modals/RankByModal';
import RankHistoryChart from './components/RankHistoryChart';
import { useRankHistoryAll } from '../../hooks/chart/useRankHistory';
import { loadLeaderboardRankBy, saveLeaderboardRankBy } from '../../utils/leaderboardSettings';
import { useModalState } from '../../hooks/ui/useModalState';
import { useIsMobileChrome, useIsWideDesktop } from '../../hooks/ui/useIsMobile';
import { useGridColumnCount } from '../../hooks/ui/useGridColumnCount';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useAppliedBandComboFilter } from '../../contexts/BandFilterActionContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { isBandFilterForSelectedProfile } from '../../state/bandFilter';
import { usePageQuickLinks, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';
import type { PageQuickLinksConfig } from '../../components/page/PageQuickLinks';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';

import {
  Display, Overflow, Gap,
  GridTemplate, Size, STAGGER_INTERVAL, FADE_DURATION, flexColumn,
} from '@festival/theme';
import { leaderboardsSlides } from './firstRun';
import { coerceRankingMetric } from './helpers/rankingHelpers';
import { coerceBandRankingMetric, getEnabledBandRankingMetrics } from './helpers/bandRankingHelpers';
import { BAND_TYPES, bandTypeLabel } from '../../utils/bandTypes';

/** Set to 1 to stagger the right column one slot (125 ms) after the left. */
const COLUMN_STAGGER_OFFSET = 1;
const QUICK_LINK_GLYPH_ICON_SIZE = 20;

type LeaderboardsQuickLink = PageQuickLinkItem;

function instrumentQuickLinkId(instrument: InstrumentKey): string {
  return `instrument:${instrument}`;
}

function bandQuickLinkId(bandType: BandType): string {
  return `band:${bandType}`;
}

function normalizeAccountId(accountId: string | null | undefined): string {
  return accountId?.trim().toLowerCase() ?? '';
}

export default function LeaderboardsOverviewPage() {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const { profile, player } = useTrackedPlayer();
  const selectedAccountId = player?.accountId;
  const selectedBand = profile?.type === 'band' ? profile : null;
  const selectedBandTeamKey = selectedBand?.teamKey;
  const selectedBandType = profile?.type === 'band' ? profile.bandType : undefined;
  const selectedBandMembers = useMemo(() => {
    if (profile?.type !== 'band') return [];
    const memberNames = new Map(profile.members.map(member => [normalizeAccountId(member.accountId), member.displayName]));
    const seen = new Set<string>();
    return profile.teamKey.split(':').flatMap(accountId => {
      const normalizedAccountId = normalizeAccountId(accountId);
      if (!normalizedAccountId || seen.has(normalizedAccountId)) return [];
      seen.add(normalizedAccountId);
      return [{
        accountId,
        displayName: memberNames.get(normalizedAccountId) || accountId.slice(0, 8),
      }];
    });
  }, [profile]);
  const selectedBandMemberAccountIds = useMemo(() => selectedBandMembers.map(member => member.accountId), [selectedBandMembers]);
  const appliedBandComboFilter = useAppliedBandComboFilter();
  const hasSelectedBandComboFilter = isBandFilterForSelectedProfile(appliedBandComboFilter, profile);
  const isMobile = useIsMobileChrome();
  const isWideDesktop = useIsWideDesktop();
  const fabSearch = useFabSearch();
  const scrollContainerRef = useScrollContainer();
  const [searchParams, setSearchParams] = useSearchParams();
  const rawMetric = searchParams.get('rankBy') ?? loadLeaderboardRankBy();
  const metric = selectedBand ? coerceBandRankingMetric(rawMetric, true) : coerceRankingMetric(rawMetric, true);
  const bandMetric = coerceBandRankingMetric(metric, true);

  const metricModal = useModalState<RankingMetric>(() => 'totalscore');

  const openMetricModal = useCallback(() => {
    metricModal.open(metric);
  }, [metricModal, metric]);

  const staggerRushRef = useRef<(() => void) | undefined>(undefined);
  const resetRush = useCallback(() => staggerRushRef.current?.(), []);

  const applyMetric = useCallback(() => {
    const nextMetric = selectedBand ? coerceBandRankingMetric(metricModal.draft, true) : coerceRankingMetric(metricModal.draft, true);
    scrollContainerRef.current?.scrollTo(0, 0);
    resetRush();
    setShouldStagger(true);
    saveLeaderboardRankBy(nextMetric);
    setSearchParams({ rankBy: nextMetric }, { replace: true });
    metricModal.close();
  }, [metricModal, resetRush, scrollContainerRef, selectedBand, setSearchParams]);

  useEffect(() => {
    if (!selectedBand || rawMetric === metric) return;
    saveLeaderboardRankBy(metric);
    setSearchParams({ rankBy: metric }, { replace: true });
  }, [metric, rawMetric, selectedBand, setSearchParams]);

  useEffect(() => {
    fabSearch.registerLeaderboardActions({ openMetric: openMetricModal });
    return () => fabSearch.registerLeaderboardActions(null);
  }, [fabSearch, openMetricModal]);

  const instruments = useMemo(() => visibleInstruments(settings), [settings]);

  // Fetch top-10 per visible instrument
  const rankingQueries = useQueries({
    queries: instruments.map((inst) => ({
      queryKey: queryKeys.rankings(inst, metric, 1, 10),
      queryFn: () => api.getRankings(inst, metric, 1, 10),
    })),
  });

  const bandTypes = useMemo(() => BAND_TYPES, []);
  const promotedBandType = useMemo(
    () => selectedBandType && bandTypes.includes(selectedBandType) ? selectedBandType : undefined,
    [bandTypes, selectedBandType],
  );
  const trailingBandTypes = useMemo(
    () => promotedBandType ? bandTypes.filter(bandType => bandType !== promotedBandType) : bandTypes,
    [bandTypes, promotedBandType],
  );

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

  const selectedBandComboId = hasSelectedBandComboFilter && selectedBandType && appliedBandComboFilter && appliedBandComboFilter.bandType === selectedBandType
    ? appliedBandComboFilter.comboId
    : undefined;

  const selectedBandRankingQuery = useQuery({
    queryKey: selectedBandType && selectedBandTeamKey
      ? queryKeys.bandRanking(selectedBandType, selectedBandTeamKey, selectedBandComboId, bandMetric)
      : ['bandRanking', 'none'],
    queryFn: () => api.getBandRanking(selectedBandType!, selectedBandTeamKey!, selectedBandComboId, bandMetric),
    enabled: !!selectedBandType && !!selectedBandTeamKey,
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

  const selectedMemberRankingsQuery = useQuery({
    queryKey: selectedBandMemberAccountIds.length > 0
      ? queryKeys.selectedMemberRankings(selectedBandMemberAccountIds, instruments, metric)
      : ['selectedMemberRankings', 'none'],
    queryFn: () => api.getSelectedMemberRankings(selectedBandMemberAccountIds, instruments, metric),
    enabled: selectedBandMemberAccountIds.length > 0 && instruments.length > 0,
    retry: false,
  });

  const selectedMemberRankingsByInstrument = useMemo(() => {
    const byAccountAndInstrument = new Map<string, AccountRankingDto>();
    for (const instrumentPayload of selectedMemberRankingsQuery.data?.instruments ?? []) {
      for (const ranking of instrumentPayload.entries) {
        byAccountAndInstrument.set(`${normalizeAccountId(ranking.accountId)}:${instrumentPayload.instrument}`, {
          ...ranking,
          instrument: ranking.instrument || instrumentPayload.instrument,
        });
      }
    }

    const result = {} as Record<InstrumentKey, AccountRankingDto[]>;
    for (const instrument of instruments) {
      const rows: AccountRankingDto[] = [];
      for (const member of selectedBandMembers) {
        const ranking = byAccountAndInstrument.get(`${normalizeAccountId(member.accountId)}:${instrument}`);
        if (ranking) rows.push({ ...ranking, displayName: ranking.displayName || member.displayName });
      }
      result[instrument] = rows;
    }
    return result;
  }, [instruments, selectedBandMembers, selectedMemberRankingsQuery.data?.instruments]);

  // Hoist rank-history loading so the page waits for graph data before staggering.
  const allHistory = useRankHistoryAll(instruments, player?.accountId, metric);
  const historyLoading = player ? instruments.some(inst => allHistory[inst]?.loading) : false;
  const historyAllCached = !player || instruments.every(inst => allHistory[inst]?.chartData != null && !allHistory[inst]?.loading);

  const leaderboardQueries = useMemo(() => [...rankingQueries, ...bandRankingQueries], [rankingQueries, bandRankingQueries]);
  const selectedMemberRankingsLoading = selectedBandMemberAccountIds.length > 0 && selectedMemberRankingsQuery.isLoading;
  const isLoading = leaderboardQueries.some(query => query.isLoading) || historyLoading || selectedMemberRankingsLoading;
  const hasCachedData = leaderboardQueries.every(query => query.data != null)
    && historyAllCached
    && (selectedBandMemberAccountIds.length === 0 || selectedMemberRankingsQuery.data != null);
  const allErrored = !isLoading && leaderboardQueries.length > 0 && leaderboardQueries.every(query => query.error);
  const { phase: loadPhase } = useLoadPhase(!isLoading, { skipAnimation: hasCachedData });

  const [shouldStagger, setShouldStagger] = useState(!hasCachedData);
  const maxEntriesPerCard = useMemo(
    () => Math.max(0, ...leaderboardQueries.map(query => query.data?.entries?.length ?? 0)),
    [leaderboardQueries],
  );
  const scrollRef = useRef<HTMLDivElement>(null);
  const [cols, gridRef] = useGridColumnCount();
  const maxSpotlightRowsPerCard = Math.max(player ? 1 : 0, selectedBandMembers.length);
  const itemsPerCard = maxEntriesPerCard + maxSpotlightRowsPerCard + 2; // header + entries + spotlight footer rows + button

  const totalAccountsByInstrument = useMemo(
    () => Object.fromEntries(instruments.map((inst, i) => [inst, rankingQueries[i]?.data?.totalAccounts ?? 0])),
    [instruments, rankingQueries],
  );

  useEffect(() => {
    if (loadPhase !== LoadPhase.ContentIn || !shouldStagger) return;
    const instrumentRows = Math.ceil(instruments.length / cols);
    const totalRows = instrumentRows + bandTypes.length;
    const chartSlot = player ? 1 : 0;
    const totalAnimTime =
      (chartSlot + totalRows * itemsPerCard + (cols - 1) * COLUMN_STAGGER_OFFSET) * STAGGER_INTERVAL + FADE_DURATION;
    const id = setTimeout(() => setShouldStagger(false), totalAnimTime);
    return () => clearTimeout(id);
  }, [bandTypes.length, cols, instruments.length, itemsPerCard, loadPhase, player, shouldStagger]);

  const s = useLeaderboardsStyles();
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: !!player, experimentalRanksEnabled: true }), [player]);
  const firstLeaderboardError = leaderboardQueries.find(query => query.error)?.error;
  const chartSlots = player ? 1 : 0;
  const instrumentRows = Math.ceil(instruments.length / cols);
  const promotedBandRows = promotedBandType ? 1 : 0;
  const getInstrumentCardStaggerOffset = useCallback((cardIndex: number) => {
    const gridRow = Math.floor(cardIndex / cols);
    const gridCol = cardIndex % cols;
    return chartSlots + promotedBandRows * itemsPerCard + gridRow * itemsPerCard + gridCol * COLUMN_STAGGER_OFFSET;
  }, [chartSlots, cols, itemsPerCard, promotedBandRows]);
  const getTrailingBandCardStaggerOffset = useCallback((bandIndex: number) => (
    chartSlots + promotedBandRows * itemsPerCard + instrumentRows * itemsPerCard + bandIndex * itemsPerCard
  ), [chartSlots, instrumentRows, itemsPerCard, promotedBandRows]);

  const renderBandRankingCard = useCallback((bandType: BandType, staggerOffset: number) => {
    const bandQuery = bandRankingQueries[bandTypes.indexOf(bandType)];
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
        staggerOffset={staggerOffset}
      />
    );
  }, [appliedBandComboFilter, bandMetric, bandRankingQueries, bandTypes, selectedAccountId, selectedBandRankingQuery.data, selectedBandType, shouldStagger]);

  const quickLinksTitle = t('rankings.quickLinks.title', 'Leaderboards Quick Links');
  const quickLinkItems = useMemo<LeaderboardsQuickLink[]>(() => {
    const rankHistoryLabel = t('rankings.quickLinks.rankHistory', 'Rank History Graph');
    return [
      ...(player ? [{ id: 'rank-history', label: rankHistoryLabel, landmarkLabel: rankHistoryLabel, icon: <IoStatsChart size={QUICK_LINK_GLYPH_ICON_SIZE} /> }] : []),
      ...(promotedBandType ? [{ id: bandQuickLinkId(promotedBandType), label: bandTypeLabel(promotedBandType, t), landmarkLabel: bandTypeLabel(promotedBandType, t), icon: <IoPeople size={QUICK_LINK_GLYPH_ICON_SIZE} /> }] : []),
      ...instruments.map(instrument => {
        const label = serverInstrumentLabel(instrument);
        return {
          id: instrumentQuickLinkId(instrument),
          label,
          landmarkLabel: label,
          icon: (
            <InstrumentIcon
              instrument={instrument}
              size={QUICK_LINK_GLYPH_ICON_SIZE}
            />
          ),
        };
      }),
      ...trailingBandTypes.map(bandType => {
        const label = bandTypeLabel(bandType, t);
        return { id: bandQuickLinkId(bandType), label, landmarkLabel: label, icon: <IoPeople size={QUICK_LINK_GLYPH_ICON_SIZE} /> };
      }),
    ];
  }, [instruments, player, promotedBandType, t, trailingBandTypes]);

  const {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  } = usePageQuickLinks<LeaderboardsQuickLink>({
    items: quickLinkItems,
    scrollContainerRef,
    isDesktopRailEnabled: isWideDesktop,
  });

  const handleModalQuickLinkSelect = useCallback((item: LeaderboardsQuickLink) => {
    if (isWideDesktop) {
      handleQuickLinkSelect(item);
      return;
    }
    closeQuickLinks();
    handleQuickLinkSelect(item);
  }, [closeQuickLinks, handleQuickLinkSelect, isWideDesktop]);

  const pageQuickLinks = useMemo<PageQuickLinksConfig | undefined>(() => {
    if (loadPhase !== LoadPhase.ContentIn || allErrored || quickLinkItems.length < 2) return undefined;
    return {
      title: quickLinksTitle,
      items: quickLinkItems,
      activeItemId,
      visible: quickLinksOpen,
      onOpen: openQuickLinks,
      onClose: closeQuickLinks,
      onSelect: (item) => handleModalQuickLinkSelect(item as LeaderboardsQuickLink),
      testIdPrefix: 'leaderboards',
    };
  }, [activeItemId, allErrored, closeQuickLinks, handleModalQuickLinkSelect, loadPhase, openQuickLinks, quickLinkItems, quickLinksOpen, quickLinksTitle]);

  const setRankHistoryRef = useCallback((element: HTMLDivElement | null) => {
    registerSectionRef('rank-history', element);
  }, [registerSectionRef]);
  const setInstrumentGridRef = useCallback((element: HTMLDivElement | null) => {
    gridRef(element);
  }, [gridRef]);

  return (
    <Page
      scrollRef={scrollRef}
      staggerRushRef={staggerRushRef}
      scrollRestoreKey="leaderboards"
      loadPhase={loadPhase}
      fabSpacer={loadPhase === LoadPhase.ContentIn && allErrored ? 'none' : 'end'}
      quickLinks={pageQuickLinks}
      firstRun={{ key: 'leaderboards', label: t('nav.leaderboards'), slides: leaderboardsSlides, gateContext: firstRunGateCtx }}
      before={
        isMobile ? undefined : (
        <PageHeader
          title={t('rankings.title')}
          actions={
            !isMobile && !allErrored ? (
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
          experimentalRanksEnabled={true}
          metrics={selectedBand ? getEnabledBandRankingMetrics(true) : undefined}
          subject={selectedBand ? 'bands' : 'players'}
        />
      }
    >
      {loadPhase === LoadPhase.ContentIn && allErrored && (() => {
        const parsed = parseApiError(String(firstLeaderboardError));
        return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
      })()}
      {loadPhase === LoadPhase.ContentIn && !allErrored && player && (
        <div ref={setRankHistoryRef} style={{ ...s.chartWrapper, ...buildStaggerStyle(shouldStagger ? 0 : null) }} onAnimationEnd={clearStaggerStyle}>
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
          {promotedBandType && (
            <div
              ref={(element) => registerSectionRef(bandQuickLinkId(promotedBandType), element)}
              data-testid="leaderboards-promoted-band-section-stack"
              style={s.bandSectionStack}
            >
              {renderBandRankingCard(promotedBandType, chartSlots)}
            </div>
          )}
          <div ref={setInstrumentGridRef} data-testid="leaderboards-instrument-grid" style={s.grid}>
            {instruments.map((inst, idx) => {
              const q = rankingQueries[idx];
              const pq = player ? playerQueries[idx] : undefined;
              const offset = getInstrumentCardStaggerOffset(idx);
              return (
                <div key={inst} ref={(element) => registerSectionRef(instrumentQuickLinkId(inst), element)} style={s.quickLinkAnchor}>
                  <RankingCard
                    instrument={inst as InstrumentKey}
                    metric={metric}
                    entries={q?.data?.entries ?? []}
                    totalAccounts={q?.data?.totalAccounts ?? 0}
                    playerRanking={pq?.data ?? null}
                    playerAccountId={player?.accountId}
                    spotlightRankings={selectedMemberRankingsByInstrument[inst] ?? []}
                    error={q?.error ? String(q.error) : null}
                    shouldStagger={shouldStagger}
                    staggerOffset={offset}
                  />
                </div>
              );
            })}
          </div>
          {trailingBandTypes.length > 0 && (
            <div data-testid="leaderboards-band-section-stack" style={s.bandSectionStack}>
              {trailingBandTypes.map((bandType, idx) => (
                <div key={bandType} ref={(element) => registerSectionRef(bandQuickLinkId(bandType), element)} style={s.quickLinkAnchor}>
                  {renderBandRankingCard(bandType, getTrailingBandCardStaggerOffset(idx))}
                </div>
              ))}
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
    quickLinkAnchor: {
      minWidth: 0,
      width: '100%',
    } as CSSProperties,
    chartWrapper: {
      marginBottom: Gap.section,
    } as CSSProperties,
  }), []);
}
