/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useEffect, useState, useCallback, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { IoOptions } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useQuery } from '@tanstack/react-query';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { RankingEntry } from './components/RankingEntry';
import { PaginatedLeaderboard } from '../../components/leaderboard/PaginatedLeaderboard';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { ActionPill } from '../../components/common/ActionPill';
import Modal from '../../components/modals/Modal';
import { ModalSection } from '../../components/modals/components/ModalSection';
import { RadioRow } from '../../components/common/RadioRow';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import type { ServerInstrumentKey as InstrumentKey, RankingMetric } from '@festival/core/api/serverTypes';
import { LoadPhase, InstrumentHeaderSize } from '@festival/core';
import { serverInstrumentLabel } from '@festival/core/api/serverTypes';
import { getRankForMetric, formatRating, getRatingForMetric, RANKING_METRICS, computeRankWidth } from './helpers/rankingHelpers';
import { rankingsCache } from '../../api/pageCache';
import { useModalState } from '../../hooks/ui/useModalState';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useFabSearch } from '../../contexts/FabSearchContext';
import {
  Gap, Layout,
  STAGGER_INTERVAL, FADE_DURATION, Size,
} from '@festival/theme';

const PAGE_SIZE = 25;

export default function FullRankingsPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();

  const instrument = (searchParams.get('instrument') ?? 'Solo_Guitar') as InstrumentKey;
  const metric = (searchParams.get('rankBy') ?? 'totalscore') as RankingMetric;
  const pageParam = Math.max(1, Number(searchParams.get('page')) || 1);

  const { player } = useTrackedPlayer();
  const isMobileChrome = useIsMobileChrome();
  const hasFab = isMobileChrome;
  const fabSearch = useFabSearch();

  const metricModal = useModalState<RankingMetric>(() => 'totalscore');

  const openMetricModal = useCallback(() => {
    metricModal.open(metric);
  }, [metricModal, metric]);

  const applyMetric = useCallback(() => {
    const m = metricModal.draft;
    metricModal.close();
    scrollRef.current?.scrollTo(0, 0);
    staggerRushRef.current?.();
    setPage(1);
    setAnimMode('paginate');
    setSearchParams({ instrument, rankBy: m, page: '1' }, { replace: true });
  }, [metricModal, instrument, setSearchParams]);

  useEffect(() => {
    fabSearch.registerLeaderboardActions({ openMetric: openMetricModal });
    return () => fabSearch.registerLeaderboardActions({ openMetric: () => {} });
  }, [fabSearch, openMetricModal]);

  const cacheKey = `${instrument}:${metric}`;
  const cached = rankingsCache.get(cacheKey);

  const [page, setPage] = useState(cached?.page ?? pageParam);

  const { data, isFetching, error } = useQuery({
    queryKey: queryKeys.rankings(instrument, metric, page, PAGE_SIZE),
    queryFn: () => api.getRankings(instrument, metric, page, PAGE_SIZE),
  });

  const { data: playerRanking } = useQuery({
    queryKey: player ? queryKeys.playerRanking(instrument, player.accountId) : ['disabled'],
    queryFn: () => api.getPlayerRanking(instrument, player!.accountId),
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
    scrollRef.current?.scrollTo(0, 0);
    staggerRushRef.current?.();
    setPage(p);
    setAnimMode('paginate');
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

  const loadPhase = isFetching ? LoadPhase.Loading : LoadPhase.ContentIn;
  const isMobile = useIsMobile();

  const skipAllAnim = !!cached;
  const [animMode, setAnimMode] = useState<'first' | 'paginate' | 'cached'>(skipAllAnim ? 'cached' : 'first');
  const scrollRef = useRef<HTMLDivElement>(null);
  const staggerRushRef = useRef<(() => void) | undefined>(undefined);

  const ROW_SLOT = Layout.entryRowHeight + Gap.sm;
  const scrollViewHeight = scrollRef.current?.clientHeight
    ?? Math.max(0, window.innerHeight - (isMobile ? 120 : 200));
  const maxVisibleRows = useMemo(
    () => Math.min(entries.length, Math.max(1, Math.ceil(scrollViewHeight / ROW_SLOT))),
    [entries.length, scrollViewHeight, ROW_SLOT],
  );

  // Retire stagger animations after they've had time to finish
  useEffect(() => {
    if (animMode === 'cached' || isFetching) return;
    const staggerWindow = maxVisibleRows * STAGGER_INTERVAL + FADE_DURATION;
    const id = setTimeout(() => setAnimMode('cached'), staggerWindow);
    return () => clearTimeout(id);
  }, [animMode, isFetching, maxVisibleRows]);

  const playerInPage = !!(player && entries.some(e => e.accountId === player.accountId));
  const hasPlayerFooter = !!playerRanking;

  // Compute rank column width from the longest rank across all visible rows
  const rankWidth = useMemo(() => {
    const allRanks = entries.map(e => getRankForMetric(e, metric));
    if (playerRanking && !playerInPage) {
      allRanks.push(getRankForMetric(playerRanking, metric));
    }
    return computeRankWidth(allRanks);
  }, [entries, playerRanking, playerInPage, metric]);

  return (
    <Page
      scrollRef={scrollRef}
      staggerRushRef={staggerRushRef}
      scrollRestoreKey={`rankings:${cacheKey}:${page}`}
      loadPhase={loadPhase}
      fabSpacer="none"
      before={
        <PageHeader
          title={
            <InstrumentHeader
              instrument={instrument}
              size={InstrumentHeaderSize.MD}
              label={`${serverInstrumentLabel(instrument)} ${t('rankings.title')}`}
            />
          }
          subtitle={data ? t('rankings.totalRanked', { count: data.totalAccounts, formattedCount: data.totalAccounts.toLocaleString() }) : undefined}
          actions={
            !isMobileChrome ? (
              <ActionPill
                icon={<IoOptions size={Size.iconAction} />}
                label={t(`rankings.metric.${metric}`)}
                onClick={openMetricModal}
                active={metric !== 'totalscore'}
              />
            ) : undefined
          }
        />
      }
      after={
        <Modal
          visible={metricModal.visible}
          title={t('rankings.rankBy')}
          onClose={metricModal.close}
          onApply={applyMetric}
          onReset={metricModal.reset}
          resetLabel={t('rankings.rankByReset')}
          resetHint={t('rankings.rankByResetHint')}
        >
          <ModalSection title={t('rankings.rankBy')} hint={t('rankings.rankByHint')}>
            {RANKING_METRICS.map((m) => (
              <RadioRow
                key={m}
                label={t(`rankings.metric.${m}`)}
                hint={t(`rankings.metric.${m}Desc`)}
                selected={metricModal.draft === m}
                onSelect={() => metricModal.setDraft(m)}
              />
            ))}
          </ModalSection>
        </Modal>
      }
    >

      {error && <div style={errorStyle}>{String(error)}</div>}

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
            songsLabel={`${e.songsPlayed} / ${e.totalChartedSongs}`}
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
              songsLabel={`${playerRanking.songsPlayed} / ${playerRanking.totalChartedSongs}`}
              isPlayer
              rankWidth={rankWidth}
            />
          </Link>
        ) : undefined}
        animMode={animMode}
        maxVisibleRows={maxVisibleRows}
        isMobile={isMobile}
        hasFab={hasFab}
      />
    </Page>
  );
}

const errorStyle = { fontSize: 14, color: '#ef4444', textAlign: 'center' as const, padding: '16px 0' };
