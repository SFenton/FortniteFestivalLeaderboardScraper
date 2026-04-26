/* eslint-disable react/forbid-dom-props -- page-level dynamic styles use inline style objects */
import { useCallback, useEffect, useMemo, useRef, type AnimationEvent, type CSSProperties } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { IoFunnel } from 'react-icons/io5';
import { LoadPhase } from '@festival/core';
import type { PlayerBandEntry, PlayerBandListGroup } from '@festival/core/api/serverTypes';
import { Colors, Font, Gap, Layout, Radius, Size, Weight, flexColumn, frostedCard } from '@festival/theme';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import EmptyState from '../../components/common/EmptyState';
import PageHeader from '../../components/common/PageHeader';
import { ActionPill } from '../../components/common/ActionPill';
import Paginator from '../../components/common/Paginator';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useModalState } from '../../hooks/ui/useModalState';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useStagger } from '../../hooks/ui/useStagger';
import Page from '../Page';
import PlayerBandCard, { formatPlayerBandNames } from '../player/components/PlayerBandCard';
import BandFilterModal, { type BandFilterDraft } from './modals/BandFilterModal';

const PLAYER_BANDS_PAGE_SIZE = 25;
const BAND_GROUPS: PlayerBandListGroup[] = ['all', 'duos', 'trios', 'quads'];

export default function PlayerBandsPage() {
  const { t } = useTranslation();
  const { accountId = '' } = useParams<{ accountId: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const scrollContainerRef = useScrollContainer();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const fabSearch = useFabSearch();

  const group = coerceBandGroup(searchParams.get('group'));
  const page = Math.max(1, Number(searchParams.get('page')) || 1);
  const routeName = searchParams.get('name')?.trim() || undefined;

  const filterModal = useModalState<BandFilterDraft>(() => 'all');

  const setRoute = useCallback((nextGroup: PlayerBandListGroup, nextPage: number) => {
    const params = new URLSearchParams();
    params.set('group', nextGroup);
    params.set('page', String(Math.max(1, nextPage)));
    if (routeName) params.set('name', routeName);
    scrollContainerRef.current?.scrollTo(0, 0);
    setSearchParams(params, { replace: true });
  }, [routeName, scrollContainerRef, setSearchParams]);

  useEffect(() => {
    const rawGroup = searchParams.get('group');
    const rawPage = searchParams.get('page');
    if (rawGroup !== group || rawPage !== String(page)) {
      setRoute(group, page);
    }
  }, [group, page, searchParams, setRoute]);

  const openFilter = useCallback(() => {
    filterModal.open(group);
  }, [filterModal, group]);

  const applyFilter = useCallback(() => {
    const nextGroup = filterModal.draft;
    filterModal.close();
    setRoute(nextGroup, 1);
  }, [filterModal, setRoute]);

  const resetFilter = useCallback(() => {
    filterModal.setDraft('all');
  }, [filterModal]);

  const openFilterRef = useRef(openFilter);
  openFilterRef.current = openFilter;
  useEffect(() => {
    fabSearch.registerBandActions({ openFilter: () => openFilterRef.current() });
    return () => fabSearch.registerBandActions({ openFilter: () => {} });
  }, [fabSearch]);

  const bandsQuery = useQuery({
    queryKey: queryKeys.playerBandsList(accountId, group, page, PLAYER_BANDS_PAGE_SIZE),
    queryFn: () => api.getPlayerBandsList(accountId, group, page, PLAYER_BANDS_PAGE_SIZE),
    enabled: !!accountId,
    staleTime: 5 * 60_000,
  });

  const data = bandsQuery.data;
  const loading = bandsQuery.isFetching && !data;
  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / PLAYER_BANDS_PAGE_SIZE)) : 0;
  const entries = useMemo(() => {
    if (!data) return [];
    if (data.entries.length <= PLAYER_BANDS_PAGE_SIZE) return data.entries;

    const start = (page - 1) * PLAYER_BANDS_PAGE_SIZE;
    return data.entries.slice(start, start + PLAYER_BANDS_PAGE_SIZE);
  }, [data, page]);

  useEffect(() => {
    if (data && data.totalCount > 0 && page > totalPages) {
      setRoute(group, totalPages);
    }
  }, [data, group, page, setRoute, totalPages]);

  const { phase, shouldStagger } = usePageTransition(`playerBands:${accountId}:${group}:${page}`, !loading);
  const { forIndex: stagger, clearAnim } = useStagger(shouldStagger);
  const styles = useStyles(isMobile);

  const title = routeName ? t('bandList.titleForName', { name: routeName }) : t('bandList.title');
  const groupLabel = t(`bandList.groups.${group}`);
  const subtitle = data
    ? t('bandList.subtitle', {
        group: groupLabel,
        count: data.totalCount,
        formattedCount: data.totalCount.toLocaleString(),
      })
    : groupLabel;

  const goToPage = useCallback((nextPage: number) => {
    if (nextPage < 1 || (totalPages && nextPage > totalPages)) return;
    setRoute(group, nextPage);
  }, [group, setRoute, totalPages]);

  const showDesktopActions = !isMobileChrome;

  return (
    <Page
      scrollRestoreKey={`playerBands:${accountId}:${group}:${page}`}
      scrollDeps={[phase, entries.length, page, group]}
      loadPhase={phase}
      containerStyle={styles.container}
      before={(
        <PageHeader
          title={title}
          subtitle={subtitle}
          actions={showDesktopActions ? (
            <ActionPill
              icon={<IoFunnel size={Size.iconAction} />}
              label={t('common.filterBands')}
              onClick={openFilter}
              active={group !== 'all'}
            />
          ) : undefined}
        />
      )}
      after={(
        <BandFilterModal
          visible={filterModal.visible}
          draft={filterModal.draft}
          savedDraft={group}
          onChange={filterModal.setDraft}
          onCancel={filterModal.close}
          onReset={resetFilter}
          onApply={applyFilter}
        />
      )}
    >
      {phase === LoadPhase.ContentIn && bandsQuery.error && (
        <EmptyState
          fullPage
          title={t('bandList.failedToLoad')}
          subtitle={bandsQuery.error instanceof Error ? bandsQuery.error.message : t('bandList.failedToLoadSubtitle')}
          style={stagger(0)}
          onAnimationEnd={clearAnim}
        />
      )}

      {phase === LoadPhase.ContentIn && !bandsQuery.error && data && (
        <div style={styles.content}>
          {entries.length === 0 ? (
            <EmptyState
              title={t('bandList.emptyTitle')}
              subtitle={t('bandList.emptySubtitle', { group: groupLabel })}
              style={stagger(0)}
              onAnimationEnd={clearAnim}
            />
          ) : (
            <div style={styles.cardGrid}>
              {entries.map((entry, index) => (
                <BandListRow
                  key={`${entry.bandType}:${entry.teamKey}`}
                  accountId={accountId}
                  entry={entry}
                  style={stagger(index)}
                  onAnimationEnd={clearAnim}
                />
              ))}
            </div>
          )}

          {totalPages > 1 && (
            <Paginator
              style={styles.pagination}
              onSkipPrev={() => goToPage(1)}
              onPrev={() => goToPage(page - 1)}
              onNext={() => goToPage(page + 1)}
              onSkipNext={() => goToPage(totalPages)}
              prevDisabled={page <= 1}
              nextDisabled={page >= totalPages}
            >
              <span style={styles.pageInfoBadge}>{page.toLocaleString()} / {totalPages.toLocaleString()}</span>
            </Paginator>
          )}
        </div>
      )}
    </Page>
  );
}

function BandListRow({
  accountId,
  entry,
  style,
  onAnimationEnd,
}: {
  accountId: string;
  entry: PlayerBandEntry;
  style?: CSSProperties;
  onAnimationEnd: (e: AnimationEvent<HTMLElement>) => void;
}) {
  const { t } = useTranslation();
  const names = formatPlayerBandNames(entry);
  const appearanceCount = entry.appearanceCount ?? 0;

  return (
    <PlayerBandCard
      entry={entry}
      sourceAccountId={accountId}
      style={style}
      onAnimationEnd={onAnimationEnd}
      ariaLabel={names ? t('bandList.viewBand', { names }) : t('band.title')}
      appearanceLabel={t('bandList.appearanceLabel', { count: appearanceCount })}
    />
  );
}

function coerceBandGroup(value: string | null): PlayerBandListGroup {
  return BAND_GROUPS.includes(value as PlayerBandListGroup) ? (value as PlayerBandListGroup) : 'all';
}

function useStyles(isMobile: boolean) {
  return useMemo(() => ({
    container: {
      paddingBottom: Layout.fabPaddingBottom,
    } as CSSProperties,
    content: {
      ...flexColumn,
      gap: Gap.section,
    } as CSSProperties,
    cardGrid: {
      display: 'grid',
      gridTemplateColumns: isMobile ? 'minmax(0, 1fr)' : 'repeat(2, minmax(0, 1fr))',
      gap: Gap.md,
    } as CSSProperties,
    pagination: {
      marginTop: Gap.sm,
      marginBottom: Gap.xl,
    } as CSSProperties,
    pageInfoBadge: {
      ...frostedCard,
      color: Colors.textPrimary,
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      padding: `${Gap.sm}px ${Gap.lg}px`,
      borderRadius: Radius.full,
    } as CSSProperties,
  }), [isMobile]);
}
