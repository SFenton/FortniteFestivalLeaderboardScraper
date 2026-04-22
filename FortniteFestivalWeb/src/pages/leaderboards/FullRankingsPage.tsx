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
import { RankingEntry } from './components/RankingEntry';
import { PaginatedLeaderboard } from '../../components/leaderboard/PaginatedLeaderboard';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { ActionPill } from '../../components/common/ActionPill';
import PageHeaderTransition from '../../components/common/PageHeaderTransition';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import type {
  ServerInstrumentKey as InstrumentKey,
  RankingMetric,
  RankingsPageResponse,
  ComboPageResponse,
  AccountRankingDto,
  ComboRankingEntry,
  AccountRankingEntry,
} from '@festival/core/api/serverTypes';
import { InstrumentHeaderSize, formatRatingValue, rankColor } from '@festival/core';
import { serverInstrumentLabel, DEFAULT_INSTRUMENT } from '@festival/core/api/serverTypes';
import { LEADERBOARD_PAGE_SIZE, getRankForMetric, formatRating, getRatingForMetric, getSongsLabel, computeRankWidth } from './helpers/rankingHelpers';
import { loadLeaderboardRankBy, saveLeaderboardRankBy } from '../../utils/leaderboardSettings';
import { rankingsCache } from '../../api/pageCache';
import { useModalState } from '../../hooks/ui/useModalState';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useFabSearch } from '../../contexts/FabSearchContext';
import EmptyState from '../../components/common/EmptyState';
import { parseApiError } from '../../utils/apiError';
import { buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import { Size, Colors, Font, Weight } from '@festival/theme';
import InstrumentPickerModal from './modals/InstrumentPickerModal';
import RankByModal from './modals/RankByModal';
import { comboScopeLabel, isRankingScopeComboId } from '../../utils/rankingScopes';

type FullRankingsData = RankingsPageResponse | ComboPageResponse;
type FullPlayerRanking = AccountRankingDto | ({ comboId: string; rankBy: string; totalAccounts: number } & ComboRankingEntry);

export default function FullRankingsPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const { settings } = useSettings();

  const rawComboId = searchParams.get('combo');
  const comboId = rawComboId && isRankingScopeComboId(rawComboId) ? rawComboId : null;
  const isCombo = comboId != null;
  const instrument = (searchParams.get('instrument') ?? 'Solo_Guitar') as InstrumentKey;
  const rawMetric = (searchParams.get('rankBy') ?? loadLeaderboardRankBy()) as RankingMetric;
  const metric = settings.enableExperimentalRanks ? rawMetric : 'totalscore' as RankingMetric;
  const pageParam = Math.max(1, Number(searchParams.get('page')) || 1);

  const { player } = useTrackedPlayer();
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
    const nextMetric = metricModal.draft;
    metricModal.close();
    saveLeaderboardRankBy(nextMetric);
    scrollContainerRef.current?.scrollTo(0, 0);
    setPage(1);
    setSearchParams(
      isCombo
        ? { combo: comboId!, rankBy: nextMetric, page: '1' }
        : { instrument, rankBy: nextMetric, page: '1' },
      { replace: true },
    );
  }, [comboId, instrument, isCombo, metricModal, scrollContainerRef, setSearchParams]);

  const applyInstrument = useCallback(() => {
    const nextInstrument = instrumentModal.draft;
    instrumentModal.close();
    scrollContainerRef.current?.scrollTo(0, 0);
    setPage(1);
    setSearchParams({ instrument: nextInstrument, rankBy: metric, page: '1' }, { replace: true });
  }, [instrumentModal, metric, scrollContainerRef, setSearchParams]);

  useEffect(() => {
    fabSearch.registerLeaderboardActions({ openMetric: openMetricModal, openInstrument: isCombo ? () => {} : openInstrumentModal });
    return () => fabSearch.registerLeaderboardActions({ openMetric: () => {}, openInstrument: () => {} });
  }, [fabSearch, isCombo, openInstrumentModal, openMetricModal]);

  const cacheKey = isCombo ? `combo:${comboId}:${metric}` : `${instrument}:${metric}`;
  const cached = rankingsCache.get(cacheKey);
  const [page, setPage] = useState(cached?.page ?? pageParam);

  const { data, isFetching, error } = useQuery<FullRankingsData>({
    queryKey: isCombo
      ? queryKeys.comboRankings(comboId!, metric, page, LEADERBOARD_PAGE_SIZE)
      : queryKeys.rankings(instrument, metric, page, LEADERBOARD_PAGE_SIZE),
    queryFn: () => isCombo
      ? api.getComboRankings(comboId!, metric, page, LEADERBOARD_PAGE_SIZE)
      : api.getRankings(instrument, metric, page, LEADERBOARD_PAGE_SIZE),
    placeholderData: (previous, previousQuery) => {
      if (!previous || !previousQuery) return undefined;
      const [queryName, scopeValue, options] = previousQuery.queryKey as [string, string, { rankBy?: string }];
      if (isCombo) {
        return queryName === 'comboRankings' && scopeValue === comboId && options.rankBy === metric ? previous : undefined;
      }
      return queryName === 'rankings' && scopeValue === instrument && options.rankBy === metric ? previous : undefined;
    },
  });

  const { data: playerRanking } = useQuery<FullPlayerRanking>({
    queryKey: player
      ? (isCombo
          ? queryKeys.playerComboRanking(player.accountId, comboId!, metric)
          : queryKeys.playerRanking(instrument, player.accountId, metric))
      : ['disabled'],
    queryFn: () => isCombo
      ? api.getPlayerComboRanking(player!.accountId, comboId!, metric)
      : api.getPlayerRanking(instrument, player!.accountId, metric),
    enabled: !!player,
  });

  const totalPages = data ? Math.ceil(data.totalAccounts / LEADERBOARD_PAGE_SIZE) : 0;
  const entries = data?.entries ?? [];
  const reserveTenDigitScoreWidth = metric === 'totalscore';

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
        : { instrument, rankBy: metric, page: String(nextPage) },
      { replace: true },
    );
  }, [comboId, instrument, isCombo, metric, scrollContainerRef, setSearchParams, totalPages]);

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

  const scrollRef = useRef<HTMLDivElement>(null);
  const staggerRushRef = useRef<(() => void) | undefined>(undefined);

  const hasPlayerFooter = !!playerRanking;

  const rankWidth = useMemo(() => {
    const allRanks = entries.map((entry) => getDisplayRank(entry, metric));
    if (!isMobile && playerRanking) {
      allRanks.push(getDisplayRank(playerRanking, metric));
    }
    return computeRankWidth(allRanks);
  }, [entries, isMobile, metric, playerRanking]);

  const playerRankWidth = useMemo(() => {
    if (!playerRanking) return undefined;
    return computeRankWidth([getDisplayRank(playerRanking, metric)]);
  }, [metric, playerRanking]);

  const pageLabel = isCombo ? comboScopeLabel(comboId!) : serverInstrumentLabel(instrument);
  const showMobilePageHeader = !isMobileChrome || settings.showButtonsInHeaderMobile;

  return (
    <Page
      scrollRef={scrollRef}
      staggerRushRef={staggerRushRef}
      scrollRestoreKey={`rankings:${cacheKey}:${page}`}
      fabSpacer="none"
      before={isMobileChrome ? (
        <PageHeaderTransition visible={showMobilePageHeader}>
          <PageHeader title={renderPageTitle(isCombo, pageLabel, t('rankings.title'), data?.totalAccounts, t)} />
        </PageHeaderTransition>
      ) : showMobilePageHeader ? (
        <PageHeader
          title={renderPageTitle(isCombo, pageLabel, t('rankings.title'), data?.totalAccounts, t, instrument)}
          actions={
            !isMobileChrome ? (
              <>
                {!isCombo && (
                  <ActionPill
                    icon={<IoMusicalNotes size={Size.iconAction} />}
                    label={serverInstrumentLabel(instrument)}
                    onClick={openInstrumentModal}
                    active={instrument !== DEFAULT_INSTRUMENT}
                  />
                )}
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
      ) : undefined}
      after={<>
        {!isCombo && (
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
        />
      </>}
    >
      {error && (() => {
        const parsed = parseApiError(String(error));
        return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
      })()}

      <PaginatedLeaderboard<AccountRankingEntry | ComboRankingEntry>
        entries={entries}
        page={page}
        totalPages={totalPages}
        onGoToPage={goToPage}
        entryKey={(entry) => entry.accountId}
        isPlayerEntry={(entry) => entry.accountId === player?.accountId}
        renderRow={(entry) => {
          const rank = getDisplayRank(entry, metric);
          const rating = getDisplayRating(entry, metric);
          const usePercentile = metric === 'adjusted' || metric === 'weighted';
          const isFcRate = metric === 'fcrate';
          const fcPct = isFcRate ? rating * 100 : 0;

          return (
            <RankingEntry
              rank={rank}
              displayName={entry.displayName ?? entry.accountId.slice(0, 8)}
              ratingLabel={formatRating(rating, metric)}
              songsLabel={getDisplaySongsLabel(entry, metric)}
              valueDisplay={usePercentile ? formatRatingValue(rating) : undefined}
              valueColor={usePercentile ? rankColor(rank, data?.totalAccounts ?? 0) : undefined}
              ratingPillTier={isFcRate ? (fcPct >= 99 ? 'top1' : fcPct >= 95 ? 'top5' : 'default') : undefined}
              songsLabelPrimary={isFcRate}
              isPlayer={entry.accountId === player?.accountId}
              rankWidth={rankWidth}
              reserveTenDigitScoreWidth={reserveTenDigitScoreWidth}
            />
          );
        }}
        entryLinkTo={(entry) => `/player/${entry.accountId}`}
        hasPlayerFooter={hasPlayerFooter}
        renderPlayerFooter={playerRanking ? ({ className, style }) => (
          <Link to={`/player/${playerRanking.accountId}`} className={className} style={style}>
            <RankingEntry
              rank={getDisplayRank(playerRanking, metric)}
              displayName={playerRanking.displayName ?? playerRanking.accountId.slice(0, 8)}
              ratingLabel={formatRating(getDisplayRating(playerRanking, metric), metric)}
              songsLabel={getDisplaySongsLabel(playerRanking, metric)}
              valueDisplay={(metric === 'adjusted' || metric === 'weighted') ? formatRatingValue(getDisplayRating(playerRanking, metric)) : undefined}
              valueColor={(metric === 'adjusted' || metric === 'weighted') ? rankColor(getDisplayRank(playerRanking, metric), data?.totalAccounts ?? 0) : undefined}
              ratingPillTier={metric === 'fcrate' ? ((getDisplayRating(playerRanking, metric) * 100) >= 99 ? 'top1' : (getDisplayRating(playerRanking, metric) * 100) >= 95 ? 'top5' : 'default') : undefined}
              songsLabelPrimary={metric === 'fcrate'}
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
        staggerRushRef={staggerRushRef}
        footerAnimKey={cacheKey}
      />
    </Page>
  );
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
