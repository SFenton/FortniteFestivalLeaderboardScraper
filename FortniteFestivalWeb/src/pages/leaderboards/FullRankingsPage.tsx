/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useEffect, useState, useCallback, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { IoOptions, IoMusicalNotes } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useQuery } from '@tanstack/react-query';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { useSettings } from '../../contexts/SettingsContext';
import { useScoreFilter } from '../../hooks/data/useScoreFilter';
import { RankingEntry } from './components/RankingEntry';
import { PaginatedLeaderboard } from '../../components/leaderboard/PaginatedLeaderboard';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { ActionPill } from '../../components/common/ActionPill';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import type { ServerInstrumentKey as InstrumentKey, RankingMetric } from '@festival/core/api/serverTypes';
import { InstrumentHeaderSize } from '@festival/core';
import { serverInstrumentLabel, DEFAULT_INSTRUMENT } from '@festival/core/api/serverTypes';
import { getRankForMetric, formatRating, getRatingForMetric, getSongsLabel, computeRankWidth } from './helpers/rankingHelpers';
import { loadLeaderboardRankBy, saveLeaderboardRankBy } from '../../utils/leaderboardSettings';
import { rankingsCache } from '../../api/pageCache';
import { useModalState } from '../../hooks/ui/useModalState';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useFabSearch } from '../../contexts/FabSearchContext';
import EmptyState from '../../components/common/EmptyState';
import { parseApiError } from '../../utils/apiError';
import { buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import { Size } from '@festival/theme';
import InstrumentPickerModal from './modals/InstrumentPickerModal';
import RankByModal from './modals/RankByModal';

const PAGE_SIZE = 25;

export default function FullRankingsPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const { settings } = useSettings();

  const instrument = (searchParams.get('instrument') ?? 'Solo_Guitar') as InstrumentKey;
  const rawMetric = (searchParams.get('rankBy') ?? loadLeaderboardRankBy()) as RankingMetric;
  const metric = settings.enableExperimentalRanks ? rawMetric : 'totalscore' as RankingMetric;
  const pageParam = Math.max(1, Number(searchParams.get('page')) || 1);

  const { player } = useTrackedPlayer();
  const { leewayParam } = useScoreFilter();
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
    const m = metricModal.draft;
    metricModal.close();
    saveLeaderboardRankBy(m);
    scrollContainerRef.current?.scrollTo(0, 0);
    setPage(1);
    setSearchParams({ instrument, rankBy: m, page: '1' }, { replace: true });
  }, [metricModal, instrument, setSearchParams]);

  const applyInstrument = useCallback(() => {
    const inst = instrumentModal.draft;
    instrumentModal.close();
    scrollContainerRef.current?.scrollTo(0, 0);
    setPage(1);
    setSearchParams({ instrument: inst, rankBy: metric, page: '1' }, { replace: true });
  }, [instrumentModal, metric, setSearchParams]);

  useEffect(() => {
    fabSearch.registerLeaderboardActions({ openMetric: openMetricModal, openInstrument: openInstrumentModal });
    return () => fabSearch.registerLeaderboardActions({ openMetric: () => {}, openInstrument: () => {} });
  }, [fabSearch, openMetricModal, openInstrumentModal]);

  const cacheKey = `${instrument}:${metric}`;
  const cached = rankingsCache.get(cacheKey);

  const [page, setPage] = useState(cached?.page ?? pageParam);

  const { data, isFetching, error } = useQuery({
    queryKey: queryKeys.rankings(instrument, metric, page, PAGE_SIZE, leewayParam),
    queryFn: () => api.getRankings(instrument, metric, page, PAGE_SIZE, leewayParam),
    // Keep previous page data visible during pagination, but NOT across
    // instrument/metric changes (those should trigger a full stagger cycle).
    placeholderData: (prev, prevQuery) => {
      if (!prev || !prevQuery) return undefined;
      const [, prevInst, { rankBy: prevRankBy }] = prevQuery.queryKey;
      return prevInst === instrument && prevRankBy === metric ? prev : undefined;
    },
  });

  const { data: playerRanking } = useQuery({
    queryKey: player ? queryKeys.playerRanking(instrument, player.accountId, leewayParam, metric) : ['disabled'],
    queryFn: () => api.getPlayerRanking(instrument, player!.accountId, leewayParam, metric),
    enabled: !!player,
  });

  const totalPages = data ? Math.ceil(data.totalAccounts / PAGE_SIZE) : 0;
  const entries = data?.entries ?? [];

  // Cache page number for scroll restoration
  useEffect(() => {
    rankingsCache.set(cacheKey, { page, scrollTop: 0 });
  }, [cacheKey, page]);

  const goToPage = useCallback((p: number) => {
    if (p < 1 || (totalPages && p > totalPages)) return;
    scrollContainerRef.current?.scrollTo(0, 0);
    setPage(p);
    setSearchParams({ instrument, rankBy: metric, page: String(p) }, { replace: true });
  }, [instrument, metric, totalPages, setSearchParams]);

  // Keyboard pagination
  useEffect(() => {
    function onKey(ev: KeyboardEvent) {
      if (ev.key === 'ArrowLeft' && page > 1) goToPage(page - 1);
      if (ev.key === 'ArrowRight' && totalPages && page < totalPages) goToPage(page + 1);
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [page, totalPages, goToPage]);

  const isMobile = useIsMobile();
  const loading = isFetching && !data;

  const scrollRef = useRef<HTMLDivElement>(null);
  const staggerRushRef = useRef<(() => void) | undefined>(undefined);

  const hasPlayerFooter = !!playerRanking;

  // Compute rank column width from the longest rank across page entries (footer computes its own)
  const rankWidth = useMemo(() => {
    const allRanks = entries.map(e => getRankForMetric(e, metric));
    return computeRankWidth(allRanks);
  }, [entries, metric]);

  const playerRankWidth = useMemo(() => {
    if (!playerRanking) return undefined;
    return computeRankWidth([getRankForMetric(playerRanking, metric)]);
  }, [playerRanking, metric]);

  return (
    <Page
      scrollRef={scrollRef}
      staggerRushRef={staggerRushRef}
      scrollRestoreKey={`rankings:${cacheKey}:${page}`}
      fabSpacer="none"
      before={
        <PageHeader
          title={
            <InstrumentHeader
              instrument={instrument}
              size={InstrumentHeaderSize.MD}
              label={`${serverInstrumentLabel(instrument)} ${t('rankings.title')}`}
              subtitle={data ? t('rankings.totalRanked', { count: data.totalAccounts, formattedCount: data.totalAccounts.toLocaleString() }) : undefined}
            />
          }
          actions={
            !isMobileChrome ? (
              <>
                <ActionPill
                  icon={<IoMusicalNotes size={Size.iconAction} />}
                  label={serverInstrumentLabel(instrument)}
                  onClick={openInstrumentModal}
                  active={instrument !== DEFAULT_INSTRUMENT}
                />
                {settings.enableExperimentalRanks && (
                  <ActionPill
                    icon={<IoOptions size={Size.iconAction} />}
                    label={t(`rankings.metric.${metric}`)}
                    onClick={openMetricModal}
                    active={metric !== 'totalscore'}
                  />
                )}
              </>
            ) : undefined
          }
        />
      }
      after={<>
        <InstrumentPickerModal
          visible={instrumentModal.visible}
          draft={instrumentModal.draft}
          savedDraft={instrument}
          onChange={instrumentModal.setDraft}
          onCancel={instrumentModal.close}
          onApply={applyInstrument}
        />
        <RankByModal
          visible={metricModal.visible}
          draft={metricModal.draft}
          onDraftChange={metricModal.setDraft}
          onClose={metricModal.close}
          onApply={applyMetric}
          onReset={metricModal.reset}
        />
      </>}
    >

      {error && (() => { const parsed = parseApiError(String(error)); return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />; })()}

      <PaginatedLeaderboard
        entries={entries}
        page={page}
        totalPages={totalPages}
        onGoToPage={goToPage}
        entryKey={(e) => e.accountId}
        isPlayerEntry={(e) => e.accountId === player?.accountId}
        renderRow={(e) => (
          <RankingEntry
            rank={getRankForMetric(e, metric)}
            displayName={e.displayName ?? e.accountId.slice(0, 8)}
            ratingLabel={formatRating(getRatingForMetric(e, metric), metric)}
            songsLabel={getSongsLabel(e, metric)}
            isPlayer={e.accountId === player?.accountId}
            rankWidth={rankWidth}
          />
        )}
        entryLinkTo={(e) => `/player/${e.accountId}`}
        hasPlayerFooter={hasPlayerFooter}
        renderPlayerFooter={playerRanking ? ({ className, style }) => (
          <Link
            to={`/player/${playerRanking.accountId}`}
            className={className}
            style={style}
          >
            <RankingEntry
              rank={getRankForMetric(playerRanking, metric)}
              displayName={playerRanking.displayName ?? playerRanking.accountId.slice(0, 8)}
              ratingLabel={formatRating(getRatingForMetric(playerRanking, metric), metric)}
              songsLabel={getSongsLabel(playerRanking, metric)}
              isPlayer
              rankWidth={playerRankWidth}
            />
          </Link>
        ) : undefined}
        loading={loading}
        cached={!!cached}
        isMobile={isMobile}
        hasFab={hasFab}
        staggerRushRef={staggerRushRef}
        footerAnimKey={`${instrument}:${metric}`}
      />
    </Page>
  );
}
