/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useEffect, useState, useCallback, useMemo, useRef, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { IoOptions } from 'react-icons/io5';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useQuery } from '@tanstack/react-query';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import { RankingEntry } from './components/RankingEntry';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { ActionPill } from '../../components/common/ActionPill';
import Modal from '../../components/modals/Modal';
import { ModalSection } from '../../components/modals/components/ModalSection';
import { RadioRow } from '../../components/common/RadioRow';
import { PaginationButton } from '../../components/common/PaginationButton';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import type { ServerInstrumentKey as InstrumentKey, RankingMetric } from '@festival/core/api/serverTypes';
import { LoadPhase, InstrumentHeaderSize } from '@festival/core';
import { serverInstrumentLabel } from '@festival/core/api/serverTypes';
import { getRankForMetric, formatRating, getRatingForMetric, RANKING_METRICS, computeRankWidth } from './helpers/rankingHelpers';
import { rankingsCache } from '../../api/pageCache';
import { useModalState } from '../../hooks/ui/useModalState';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { staggerDelay } from '@festival/ui-utils';
import {
  Colors, Font, Gap, Radius, Layout, MaxWidth,
  Display, Align, Justify, Overflow, CssValue, CssProp, TextAlign,
  Position, BoxSizing,
  frostedCard, flexRow, flexColumn, transition, padding, border, Border,
  FAST_FADE_MS, STAGGER_INTERVAL, FADE_DURATION, Size,
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
  const s = useFullRankingsStyles();

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

      {error && <div style={s.errorMsg}>{String(error)}</div>}

      <div style={s.list}>
        {entries.map((e, i) => {
          const rank = getRankForMetric(e, metric);
          const isPlayer = e.accountId === player?.accountId;
          const rowStyle = isPlayer ? s.playerEntryRow : s.entryRow;
          const delay = animMode === 'cached' ? null : (staggerDelay(i, STAGGER_INTERVAL, maxVisibleRows) ?? 0);
          const staggerStyle: CSSProperties | undefined = delay != null
            ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${delay}ms forwards` }
            : undefined;
          return (
            <Link
              key={e.accountId}
              to={`/player/${e.accountId}`}
              style={{ ...rowStyle, ...staggerStyle }}
              onAnimationEnd={(ev) => {
                const el = ev.currentTarget;
                el.style.opacity = '';
                el.style.animation = '';
              }}
            >
              <RankingEntry
                rank={rank}
                displayName={e.displayName ?? e.accountId.slice(0, 8)}
                ratingLabel={formatRating(getRatingForMetric(e, metric), metric)}
                songsLabel={`${e.songsPlayed} / ${e.totalChartedSongs}`}
                isPlayer={isPlayer}
                rankWidth={rankWidth}
              />
            </Link>
          );
        })}
        {/* Player row below page entries if not visible on current page */}
        {playerRanking && !playerInPage && (
          <Link
            to={`/player/${playerRanking.accountId}`}
            style={s.playerEntryRow}
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
        )}
      </div>

      {totalPages > 1 && (() => {
        const paginationStyle = isMobile ? s.paginationMobile : s.pagination;
        return (
          <div style={paginationStyle}>
            <PaginationButton disabled={page === 1} onClick={() => goToPage(1)}>
              {t('leaderboard.first')}
            </PaginationButton>
            <PaginationButton disabled={page === 1} onClick={() => goToPage(page - 1)}>
              {t('leaderboard.prev')}
            </PaginationButton>
            <span style={s.pageInfo}>
              <span style={s.pageInfoBadge}>
                {page.toLocaleString()} / {totalPages.toLocaleString()}
              </span>
            </span>
            <PaginationButton disabled={page >= totalPages} onClick={() => goToPage(page + 1)}>
              {t('leaderboard.next')}
            </PaginationButton>
            <PaginationButton disabled={page >= totalPages} onClick={() => goToPage(totalPages)}>
              {t('leaderboard.last')}
            </PaginationButton>
          </div>
        );
      })()}
    </Page>
  );
}

function useFullRankingsStyles() {
  return useMemo(() => {
    const entryBase: CSSProperties = {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(0, Gap.xl),
      height: Layout.entryRowHeight,
      borderRadius: Radius.md,
      textDecoration: CssValue.none,
      color: CssValue.inherit,
      transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
      fontSize: Font.md,
    };
    return {
      list: {
        ...flexColumn,
        gap: Gap.sm,
        overflow: Overflow.hidden,
      } as CSSProperties,
      entryRow: { ...entryBase } as CSSProperties,
      playerEntryRow: {
        ...entryBase,
        backgroundColor: Colors.purpleHighlight,
        border: border(Border.thin, Colors.purpleHighlightBorder),
      } as CSSProperties,
      pagination: {
        ...flexRow,
        justifyContent: Justify.center,
        gap: Gap.md,
        flexShrink: 0,
        padding: padding(Gap.md, Layout.paddingHorizontal),
        maxWidth: MaxWidth.card,
        margin: CssValue.marginCenter,
        width: CssValue.full,
        boxSizing: BoxSizing.borderBox,
        position: Position.relative,
        zIndex: 1,
      } as CSSProperties,
      paginationMobile: {
        ...flexRow,
        justifyContent: Justify.between,
        gap: Gap.none,
        flexShrink: 0,
        padding: padding(Gap.md, Layout.paddingHorizontal),
        maxWidth: MaxWidth.card,
        margin: CssValue.marginCenter,
        width: CssValue.full,
        boxSizing: BoxSizing.borderBox,
        position: Position.relative,
        zIndex: 1,
      } as CSSProperties,
      pageInfo: {
        textAlign: TextAlign.center,
      } as CSSProperties,
      pageInfoBadge: {
        ...frostedCard,
        display: Display.inlineFlex,
        alignItems: Align.center,
        justifyContent: Justify.center,
        fontSize: Font.sm,
        color: Colors.textSecondary,
        padding: padding(Gap.md, Gap.xl),
        borderRadius: Radius.sm,
        backgroundColor: Colors.backgroundCard,
      } as CSSProperties,
      errorMsg: {
        fontSize: Font.md,
        color: Colors.statusRed,
        textAlign: TextAlign.center,
        padding: padding(Gap.lg, 0),
      } as CSSProperties,
    };
  }, []);
}
