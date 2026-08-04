import { useEffect, useState, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useQueries, useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { remoteDataQueryPolicy } from '../../api/queryPolicy';
import type { PageQuickLinksConfig } from '../../components/page/PageQuickLinks';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { usePageQuickLinks, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useSetPageReady } from '../../contexts/PageReadyContext';
import { staggerCompletionDelay, useStagger } from '../../hooks/ui/useStagger';
import EmptyState from '../../components/common/EmptyState';
import PageHeader from '../../components/common/PageHeader';
import SearchModal from '../../components/search/SearchModal';
import CardPressable from '../../components/common/CardPressable';

import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import { useIsMobileChrome, useIsWideDesktop } from '../../hooks/ui/useIsMobile';
import { IoChevronForward, IoCompass, IoMusicalNotes, IoOptions, IoPeople, IoSearch, IoTrophy } from 'react-icons/io5';
import { InstrumentHeaderSize } from '@festival/core/runtime';
import { LoadPhase } from '@festival/core/runtime';
import { Gap, Size, flexColumn } from '@festival/theme';
import { serverInstrumentLabel, type AccountSearchResult, type RivalsListResponse, type ServerInstrumentKey, type RankingMetric } from '@festival/core/api';
import type { RivalSummary } from '@festival/core/api';
import { deriveRivalScopeFromSettings, isProDrumsRivalScope } from './helpers/comboUtils';
import { RIVAL_COMBO_SCOPE_SETTINGS } from './helpers/rivalRouteState';
import RivalRow from './components/RivalRow';
import { Routes } from '../../routes';
import fx from '../../styles/effects.module.css';
import { useRivalsSharedStyles } from './useRivalsSharedStyles';
import Page from '../Page';
import { rivalsSlides } from './firstRun';
import LeaderboardRivalsTab, { type LeaderboardRivalQuickLink } from './LeaderboardRivalsTab';
import { ActionPill } from '../../components/common/ActionPill';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useModalState } from '../../hooks/ui/useModalState';
import RankByModal from '../leaderboards/modals/RankByModal';
import { coerceRankingMetric } from '../leaderboards/helpers/rankingHelpers';

type InstrumentRivals = {
  instrument: ServerInstrumentKey;
  data: RivalsListResponse | null;
  loading: boolean;
  error: string | null;
};

type RivalQuickLink = PageQuickLinkItem & {
  id: 'common' | 'combo' | ServerInstrumentKey;
};

const QUICK_LINK_GLYPH_ICON_SIZE = 20;

export default function RivalsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const { settings } = useSettings();
  const { player } = useTrackedPlayer();
  const isMobile = useIsMobileChrome();
  const isWideDesktop = useIsWideDesktop();
  const scrollContainerRef = useScrollContainer();
  const accountId = player?.accountId;
  const fabSearch = useFabSearch();

  const experimentalRanksEnabled = settings.enableExperimentalRanks;
  const activeTab = (searchParams.get('tab') === 'leaderboard' ? 'leaderboard' : 'song') as 'song' | 'leaderboard';
  const rankBy = coerceRankingMetric(searchParams.get('rankBy'), experimentalRanksEnabled);
  const setTab = useCallback((tab: 'song' | 'leaderboard') => {
    const params: Record<string, string> = {};
    if (tab === 'leaderboard') { params.tab = 'leaderboard'; if (rankBy !== 'totalscore') params.rankBy = rankBy; }
    setSearchParams(params, { replace: true });
  }, [setSearchParams, rankBy]);
  const setRankBy = useCallback((metric: RankingMetric) => {
    const nextMetric = coerceRankingMetric(metric, experimentalRanksEnabled);
    const params: Record<string, string> = { tab: 'leaderboard' };
    if (nextMetric !== 'totalscore') params.rankBy = nextMetric;
    setSearchParams(params, { replace: true });
  }, [experimentalRanksEnabled, setSearchParams]);
  useEffect(() => {
    if (activeTab !== 'leaderboard' || searchParams.get('rankBy') === rankBy) return;
    const params: Record<string, string> = { tab: 'leaderboard' };
    if (rankBy !== 'totalscore') params.rankBy = rankBy;
    setSearchParams(params, { replace: true });
  }, [activeTab, rankBy, searchParams, setSearchParams]);

  const metricModal = useModalState<RankingMetric>(() => 'totalscore');
  const [findRivalSearchVisible, setFindRivalSearchVisible] = useState(false);

  const openMetricModal = useCallback(() => {
    metricModal.open(rankBy);
  }, [metricModal, rankBy]);

  const applyMetric = useCallback(() => {
    setRankBy(metricModal.draft);
    metricModal.close();
  }, [metricModal, setRankBy]);

  const toggleTab = useCallback(() => {
    setTab(activeTab === 'song' ? 'leaderboard' : 'song');
  }, [activeTab, setTab]);

  const openFindRivalSearch = useCallback(() => {
    setFindRivalSearchVisible(true);
  }, []);

  const closeFindRivalSearch = useCallback(() => {
    setFindRivalSearchVisible(false);
  }, []);

  const activeInstruments = visibleInstruments(settings);
  const combo = useMemo(() => deriveRivalScopeFromSettings(settings), [settings]);
  const rivalsScopeKey = `${accountId ?? ''}:${activeInstruments.join(',')}:${combo ?? 'none'}`;
  const noRivalsSubtitle = useMemo(() => {
    if (activeInstruments.length === 0) return undefined;
    return t(activeInstruments.length === 1 ? 'rivals.noRivalsSubtitleSingle' : 'rivals.noRivalsSubtitlePlural');
  }, [activeInstruments.length, t]);
  const comboDisplayLabel = useMemo(
    () => isProDrumsRivalScope(combo) ? t('rivals.proDrumsFamily') : t('rivals.combo'),
    [combo, t],
  );

  const instrumentQueries = useQueries({
    queries: activeInstruments.map(instrument => ({
      queryKey: queryKeys.rivalsList(accountId ?? '', instrument),
      queryFn: ({ signal }: { signal: AbortSignal }) => api.getRivalsList(accountId!, instrument, { signal }),
      enabled: !!accountId,
      ...remoteDataQueryPolicy,
    })),
  });
  const comboQuery = useQuery({
    queryKey: queryKeys.rivalsList(accountId ?? '', combo ?? ''),
    queryFn: ({ signal }) => api.getRivalsList(accountId!, combo!, { signal }),
    enabled: !!accountId && !!combo,
    ...remoteDataQueryPolicy,
  });
  const instrumentRivals = useMemo<InstrumentRivals[]>(() => (
    activeInstruments.map((instrument, index) => {
      const query = instrumentQueries[index];
      return {
        instrument,
        data: query?.data ?? null,
        loading: query?.isPending ?? true,
        error: query?.error
          ? (query.error instanceof Error ? query.error.message : 'Error')
          : null,
      };
    })
  ), [activeInstruments, instrumentQueries]);
  const comboRivals: RivalsListResponse | null = comboQuery.data ?? null;
  const comboLoading = !!combo && comboQuery.isPending;
  const mountedWithDataRef = useRef(
    !!accountId
      && activeInstruments.every((_, index) => (
        instrumentQueries[index]?.data !== undefined || instrumentQueries[index]?.isError
      ))
      && (!combo || comboQuery.data !== undefined || comboQuery.isError),
  );
  const initialRivalsScopeRef = useRef(rivalsScopeKey);
  const hasCachedData = initialRivalsScopeRef.current === rivalsScopeKey && mountedWithDataRef.current;
  const [leaderboardQuickLinkItems, setLeaderboardQuickLinkItems] = useState<LeaderboardRivalQuickLink[]>([]);
  const [leaderboardRailRevealDelayMs, setLeaderboardRailRevealDelayMs] = useState(0);
  const handleLeaderboardQuickLinksChange = useCallback((items: LeaderboardRivalQuickLink[]) => {
    setLeaderboardQuickLinkItems(current => (
      current.length === items.length
      && current.every((item, index) => (
        item.id === items[index]?.id
        && item.landmarkLabel === items[index]?.landmarkLabel
      ))
        ? current
        : items
    ));
  }, []);

  // Register toggle action for FAB and sync active tab
  const toggleTabRef = useRef(toggleTab);
  const findRivalRef = useRef(openFindRivalSearch);
  toggleTabRef.current = toggleTab;
  findRivalRef.current = openFindRivalSearch;
  /* v8 ignore start — FAB registration */
  useEffect(() => {
    fabSearch.registerRivalsActions({ toggleTab: () => toggleTabRef.current(), findRival: () => findRivalRef.current() });
    return () => fabSearch.registerRivalsActions(null);
  }, [fabSearch]);
  useEffect(() => {
    fabSearch.setRivalsActiveTab(activeTab);
  }, [fabSearch, activeTab]);
  /* v8 ignore stop */

  const allInstrumentsReady = activeInstruments.length === 0 || instrumentRivals.every(r => !r.loading);
  const comboReady = !combo || !comboLoading;
  const allReady = allInstrumentsReady && comboReady;

  // Common rivals: rivals that appear in ALL loaded instrument lists (2+ instruments)
  /* v8 ignore start -- common rivals intersection logic */
  const commonRivals = useMemo<{ above: RivalSummary[]; below: RivalSummary[] }>(() => {
    const loaded = instrumentRivals.filter(r => r.data);
    if (loaded.length < 2) return { above: [], below: [] };

    // Build a map of accountId → count of instruments where they appear
    const countMap = new Map<string, number>();
    const summaryMap = new Map<string, { above: RivalSummary[]; below: RivalSummary[] }>();
    for (const entry of loaded) {
      const seen = new Set<string>();
      for (const rival of [...entry.data!.above, ...entry.data!.below]) {
        if (seen.has(rival.accountId)) continue;
        seen.add(rival.accountId);
        countMap.set(rival.accountId, (countMap.get(rival.accountId) ?? 0) + 1);
        if (!summaryMap.has(rival.accountId)) summaryMap.set(rival.accountId, { above: [], below: [] });
        const bucket = summaryMap.get(rival.accountId)!;
        if (entry.data!.above.some(r => r.accountId === rival.accountId)) bucket.above.push(rival);
        else bucket.below.push(rival);
      }
    }

    // Keep only rivals present in ALL loaded instruments
    const threshold = loaded.length;
    const above: RivalSummary[] = [];
    const below: RivalSummary[] = [];
    for (const [accountId, count] of countMap) {
      if (count < threshold) continue;
      const bucket = summaryMap.get(accountId)!;
      // Determine direction: majority vote across instruments
      const dir = bucket.above.length >= bucket.below.length ? 'above' : 'below';
      // Pick the best summary (highest sharedSongCount) for display
      const allEntries = [...bucket.above, ...bucket.below];
      const best = allEntries.reduce((a, b) => a.sharedSongCount >= b.sharedSongCount ? a : b);
      (dir === 'above' ? above : below).push(best);
    }

    // Sort each group by rivalScore descending (most competitive first)
    above.sort((a, b) => b.rivalScore - a.rivalScore);
    below.sort((a, b) => b.rivalScore - a.rivalScore);
    return { above, below };
  }, [instrumentRivals]);
  /* v8 ignore stop */

  const { phase, shouldStagger } = usePageTransition(`rivals:${rivalsScopeKey}`, allReady, hasCachedData);
  useSetPageReady(phase === LoadPhase.ContentIn);
  const { forDelay: stagger, next: nextStagger, clearAnim } = useStagger(shouldStagger);
  const shared = useRivalsSharedStyles();
  const styles = useMemo(() => ({
    ...shared,
  }), [shared]);

  /* v8 ignore start -- render-time helpers */
  /** Compute CSS variable for min name width based on longest name in a rival list. */
  const nameWidthVar = (rivals: RivalSummary[]): React.CSSProperties => {
    const maxLen = rivals.reduce((max, r) => Math.max(max, (r.displayName ?? 'Unknown Player').length), 0);
    return { '--rival-name-width': `${Math.ceil(maxLen * 0.85)}ch` } as React.CSSProperties;
  };

  const navigateToRival = (rivalId: string, rivalName?: string | null) => {
    navigate(Routes.rivalDetail(rivalId, rivalName ?? undefined), { state: { combo, rivalName } });
  };

  const handleFindRivalSelect = (player: AccountSearchResult) => {
    navigate(Routes.rivalDetail(player.accountId, player.displayName), {
      state: { comboScope: RIVAL_COMBO_SCOPE_SETTINGS, rivalName: player.displayName, allowLiveFallback: true },
    });
  };
  /* v8 ignore stop */

  const PREVIEW_COUNT = 3;

  /* v8 ignore start -- computed render state */
  const hasAnyRivals = instrumentRivals.some(r => r.data && (r.data.above.length > 0 || r.data.below.length > 0))
    || (comboRivals && (comboRivals.above.length > 0 || comboRivals.below.length > 0))
    || (commonRivals.above.length > 0 || commonRivals.below.length > 0);
  /* v8 ignore stop */

  const songTabDesktopRailRevealDelayMs = useMemo(() => {
    if (!shouldStagger) {
      return 0;
    }

    let staggerItemCount = 0;
    const addSection = (aboveCount: number, belowCount: number) => {
      staggerItemCount += Math.min(PREVIEW_COUNT, aboveCount) + Math.min(PREVIEW_COUNT, belowCount) + 2;
    };

    if (commonRivals.above.length > 0 || commonRivals.below.length > 0) {
      addSection(commonRivals.above.length, commonRivals.below.length);
    }

    if (combo && comboRivals && (comboRivals.above.length > 0 || comboRivals.below.length > 0)) {
      addSection(comboRivals.above.length, comboRivals.below.length);
    }

    for (const entry of instrumentRivals) {
      if (!entry.data || (entry.data.above.length === 0 && entry.data.below.length === 0)) {
        continue;
      }

      addSection(entry.data.above.length, entry.data.below.length);
    }

    return staggerCompletionDelay(staggerItemCount);
  }, [combo, comboRivals, commonRivals.above.length, commonRivals.below.length, instrumentRivals, shouldStagger]);

  const quickLinkItems = useMemo<RivalQuickLink[]>(() => {
    if (activeTab === 'leaderboard') {
      return leaderboardQuickLinkItems;
    }

    const links: RivalQuickLink[] = [];

    if (commonRivals.above.length > 0 || commonRivals.below.length > 0) {
      const commonLabel = t('rivals.commonRivalsShort', 'Common Rivals');
      links.push({
        id: 'common',
        label: commonLabel,
        landmarkLabel: commonLabel,
        icon: <IoPeople size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      });
    }

    if (combo && comboRivals && (comboRivals.above.length > 0 || comboRivals.below.length > 0)) {
      const comboLabel = t('rivals.instrumentRivalsShort', { instrument: comboDisplayLabel });
      links.push({
        id: 'combo',
        label: comboLabel,
        landmarkLabel: comboLabel,
        icon: <IoMusicalNotes size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      });
    }

    for (const entry of instrumentRivals) {
      if (!entry.data || (entry.data.above.length === 0 && entry.data.below.length === 0)) {
        continue;
      }

      const instrumentLabel = t('rivals.instrumentRivalsShort', { instrument: serverInstrumentLabel(entry.instrument) });
      links.push({
        id: entry.instrument,
        label: instrumentLabel,
        landmarkLabel: instrumentLabel,
        icon: (
          <InstrumentIcon
            instrument={entry.instrument}
            size={QUICK_LINK_GLYPH_ICON_SIZE}
          />
        ),
      });
    }

    return links;
  }, [activeTab, combo, comboDisplayLabel, comboRivals, commonRivals.above.length, commonRivals.below.length, instrumentRivals, leaderboardQuickLinkItems, t]);

  const {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  } = usePageQuickLinks<RivalQuickLink>({
    items: quickLinkItems,
    scrollContainerRef,
    isDesktopRailEnabled: isWideDesktop,
  });

  const handleModalQuickLinkSelect = useCallback((link: RivalQuickLink) => {
    closeQuickLinks();
    handleQuickLinkSelect(link);
  }, [closeQuickLinks, handleQuickLinkSelect]);

  const pageQuickLinks = useMemo<PageQuickLinksConfig | undefined>(() => {
    if (phase !== LoadPhase.ContentIn || quickLinkItems.length < 2) {
      return undefined;
    }

    return {
      title: t('rivals.quickLinks'),
      items: quickLinkItems,
      activeItemId,
      visible: quickLinksOpen,
      onOpen: openQuickLinks,
      onClose: closeQuickLinks,
      desktopRailRevealDelayMs: isWideDesktop
        ? (activeTab === 'song' ? songTabDesktopRailRevealDelayMs : leaderboardRailRevealDelayMs)
        : 0,
      onSelect: (item) => {
        const nextItem = item as RivalQuickLink;
        if (isWideDesktop) {
          handleQuickLinkSelect(nextItem);
          return;
        }
        handleModalQuickLinkSelect(nextItem);
      },
      testIdPrefix: 'rivals',
    };
  }, [activeItemId, activeTab, closeQuickLinks, handleModalQuickLinkSelect, handleQuickLinkSelect, isWideDesktop, leaderboardRailRevealDelayMs, openQuickLinks, phase, quickLinkItems, quickLinksOpen, songTabDesktopRailRevealDelayMs, t]);

  const compactQuickLinksAction = !isWideDesktop && !isMobile && pageQuickLinks
    ? (
      <ActionPill
        icon={<IoCompass size={Size.iconAction} />}
        label={t('rivals.quickLinks')}
        onClick={openQuickLinks}
      />
    )
    : null;

  const toggleTabAction = (
    <ActionPill
      icon={activeTab === 'song' ? <IoTrophy size={Size.iconAction} /> : <IoMusicalNotes size={Size.iconAction} />}
      label={activeTab === 'song' ? t('rivals.tabLeaderboard') : t('rivals.tabSong')}
      onClick={toggleTab}
    />
  );

  const findRivalAction = (
    <ActionPill
      icon={<IoSearch size={Size.iconAction} />}
      label={t('rivals.findRival')}
      onClick={openFindRivalSearch}
    />
  );

  /* v8 ignore start -- JSX render tree */
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: true }), []);

  /* v8 ignore start -- guard + computed state */
  if (!accountId) {
    return <div style={styles.center}>{t('rivals.noPlayer')}</div>;
  }
  /* v8 ignore stop */

  return (
    <Page
      scrollRestoreKey={`rivals:${accountId}`}
      scrollDeps={[phase]}
      loadPhase={phase}
      containerStyle={styles.container}
      quickLinks={pageQuickLinks}
      before={
        !isMobile ? (
          <PageHeader
            title={activeTab === 'song' ? t('rivals.tabSong') : t('rivals.tabLeaderboard')}
            actions={phase === LoadPhase.ContentIn ? (
              <>
                {findRivalAction}
                {toggleTabAction}
                {compactQuickLinksAction}
                {activeTab === 'leaderboard' && experimentalRanksEnabled && (
                  <ActionPill
                    icon={<IoOptions size={Size.iconAction} />}
                    label={t(`rankings.metric.${rankBy}`)}
                    onClick={openMetricModal}
                    active={rankBy !== 'totalscore'}
                  />
                )}
              </>
            ) : undefined}
          />
        ) : undefined
      }
      firstRun={{ key: 'rivals', label: t('rivals.title'), slides: rivalsSlides, gateContext: firstRunGateCtx }}
      fabSpacer={phase === LoadPhase.ContentIn && !hasAnyRivals ? 'none' : 'end'}
      after={
        <>
          <RankByModal
            visible={metricModal.visible}
            draft={metricModal.draft}
            onDraftChange={metricModal.setDraft}
            onClose={metricModal.close}
            onApply={applyMetric}
            onReset={metricModal.reset}
            experimentalRanksEnabled={experimentalRanksEnabled}
          />
          <SearchModal
            visible={findRivalSearchVisible}
            onClose={closeFindRivalSearch}
            availableTargets={['players']}
            onPlayerSelect={handleFindRivalSelect}
          />
        </>
      }
    >
      {phase === LoadPhase.ContentIn && (
            <>
              {activeTab === 'song' ? (
            <div style={{ ...flexColumn, gap: Gap.section }}>
              {!hasAnyRivals && (
                <EmptyState fullPage title={t('rivals.noRivals')} subtitle={noRivalsSubtitle} style={stagger(200)} onAnimationEnd={clearAnim} />
              )}

              {/* Common rivals (appears in ALL selected instruments, 2+ required) */}
              {(commonRivals.above.length > 0 || commonRivals.below.length > 0) && (() => {
                const previewAbove = commonRivals.above.slice(0, PREVIEW_COUNT);
                const previewBelow = commonRivals.below.slice(0, PREVIEW_COUNT);
                const allPreview = [...previewAbove, ...previewBelow];
                const navigateToCommon = () => navigate(Routes.allRivals('common'), { state: { from: 'rivals' } });
                return (
                <div ref={(element) => registerSectionRef('common', element)} style={styles.section}>
                  <CardPressable
                    className={fx.sectionHeaderClickable}
                    style={{ ...styles.sectionHeaderClickable, ...nextStagger() }}
                    pressedStyle={styles.pressablePressed}
                    onAnimationEnd={clearAnim}
                    onPress={navigateToCommon}
                  >
                    <div style={styles.cardHeaderText}>
                      <span style={styles.cardTitle}>{t('rivals.commonRivalsShort', 'Common Rivals')}</span>
                    </div>
                    <span style={styles.seeAll}>{t('rivals.seeAll', 'See All')}</span>
                    <IoChevronForward size={20} style={styles.chevron} />
                  </CardPressable>
                  <div style={{ ...styles.rivalList, ...nameWidthVar(allPreview) }}>
                    {allPreview.map(rival => (
                      <RivalRow
                        key={rival.accountId}
                        rival={rival}
                        direction={previewAbove.includes(rival) ? 'above' : 'below'}
                        onClick={() => navigateToRival(rival.accountId, rival.displayName)}
                        style={nextStagger()}
                        onAnimationEnd={clearAnim}
                      />
                    ))}
                    <CardPressable style={{ ...styles.viewAllButton, ...nextStagger() }} pressedStyle={styles.pressablePressed} onAnimationEnd={clearAnim} onPress={navigateToCommon}>
                      {t('rivals.viewAllRivals')}
                    </CardPressable>
                  </div>
                </div>
                );
              })()}

              {/* Combo section (if 2+ instruments enabled) */}
              {combo && comboRivals && (comboRivals.above.length > 0 || comboRivals.below.length > 0) && (() => {
                const previewAbove = comboRivals.above.slice(0, PREVIEW_COUNT);
                const previewBelow = comboRivals.below.slice(0, PREVIEW_COUNT);
                const allPreview = [...previewAbove, ...previewBelow];
                const navigateToCombo = () => navigate(Routes.allRivals('combo'), { state: { from: 'rivals' } });
                return (
                <div ref={(element) => registerSectionRef('combo', element)} style={styles.section}>
                  <CardPressable
                    className={fx.sectionHeaderClickable}
                    style={{ ...styles.sectionHeaderClickable, ...nextStagger() }}
                    pressedStyle={styles.pressablePressed}
                    onAnimationEnd={clearAnim}
                    onPress={navigateToCombo}
                  >
                    <div style={styles.cardHeaderText}>
                      <span style={styles.cardTitle}>{t('rivals.instrumentRivalsShort', { instrument: comboDisplayLabel })}</span>
                    </div>
                    <span style={styles.seeAll}>{t('rivals.seeAll', 'See All')}</span>
                    <IoChevronForward size={20} style={styles.chevron} />
                  </CardPressable>
                  <div style={{ ...styles.rivalList, ...nameWidthVar(allPreview) }}>
                    {allPreview.map(rival => (
                      <RivalRow
                        key={rival.accountId}
                        rival={rival}
                        direction={previewAbove.includes(rival) ? 'above' : 'below'}
                        onClick={() => navigateToRival(rival.accountId, rival.displayName)}
                        style={nextStagger()}
                        onAnimationEnd={clearAnim}
                      />
                    ))}
                    <CardPressable style={{ ...styles.viewAllButton, ...nextStagger() }} pressedStyle={styles.pressablePressed} onAnimationEnd={clearAnim} onPress={navigateToCombo}>
                      {t('rivals.viewAllRivals')}
                    </CardPressable>
                  </div>
                </div>
                );
              })()}

              {/* Per-instrument sections */}
              {instrumentRivals.map(entry => {
                if (!entry.data || (entry.data.above.length === 0 && entry.data.below.length === 0)) return null;
                const previewAbove = entry.data.above.slice(0, PREVIEW_COUNT);
                const previewBelow = entry.data.below.slice(0, PREVIEW_COUNT);
                const allPreview = [...previewAbove, ...previewBelow];
                const navigateToInstrument = () => navigate(Routes.allRivals(entry.instrument), { state: { from: 'rivals' } });
                return (
                  <div key={entry.instrument} ref={(element) => registerSectionRef(entry.instrument, element)} style={styles.section}>
                    <CardPressable
                      className={fx.sectionHeaderClickable}
                      style={{ ...styles.sectionHeaderClickable, ...nextStagger() }}
                      pressedStyle={styles.pressablePressed}
                      onAnimationEnd={clearAnim}
                      onPress={navigateToInstrument}
                    >
                      <InstrumentHeader instrument={entry.instrument} size={InstrumentHeaderSize.SM} iconOnly />
                      <div style={styles.cardHeaderText}>
                        <span style={styles.cardTitle}>{t('rivals.instrumentRivalsShort', { instrument: serverInstrumentLabel(entry.instrument) })}</span>
                      </div>
                      <span style={styles.seeAll}>{t('rivals.seeAll', 'See All')}</span>
                      <IoChevronForward size={20} style={styles.chevron} />
                    </CardPressable>
                    <div style={{ ...styles.rivalList, ...nameWidthVar(allPreview) }}>
                      {allPreview.map(rival => (
                        <RivalRow
                          key={rival.accountId}
                          rival={rival}
                          direction={previewAbove.includes(rival) ? 'above' : 'below'}
                          onClick={() => navigateToRival(rival.accountId, rival.displayName)}
                          style={nextStagger()}
                          onAnimationEnd={clearAnim}
                        />
                      ))}
                      <CardPressable style={{ ...styles.viewAllButton, ...nextStagger() }} pressedStyle={styles.pressablePressed} onAnimationEnd={clearAnim} onPress={navigateToInstrument}>
                        {t('rivals.viewAllRivals')}
                      </CardPressable>
                    </div>
                  </div>
                );
              })}
            </div>
              ) : (
                <LeaderboardRivalsTab
                  accountId={accountId}
                  shouldStagger={shouldStagger}
                  rankBy={rankBy}
                  registerSectionRef={registerSectionRef}
                  onQuickLinksChange={handleLeaderboardQuickLinksChange}
                  onDesktopRailRevealDelayChange={setLeaderboardRailRevealDelayMs}
                />
              )}
            </>
      )}
    </Page>
  );
}

/* v8 ignore stop */
