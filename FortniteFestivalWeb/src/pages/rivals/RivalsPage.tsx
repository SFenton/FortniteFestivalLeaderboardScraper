/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../../api/client';
import type { PageQuickLinksConfig } from '../../components/page/PageQuickLinks';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { usePageQuickLinks, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useStagger } from '../../hooks/ui/useStagger';
import EmptyState from '../../components/common/EmptyState';
import PageHeader from '../../components/common/PageHeader';

import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import { useIsMobileChrome, useIsWideDesktop } from '../../hooks/ui/useIsMobile';
import { IoChevronForward, IoCompass, IoMusicalNotes, IoOptions, IoTrophy } from 'react-icons/io5';
import { InstrumentHeaderSize } from '@festival/core';
import { LoadPhase } from '@festival/core';
import { Gap, Size, flexColumn } from '@festival/theme';
import { serverInstrumentLabel, type RivalsListResponse, type ServerInstrumentKey, type RankingMetric } from '@festival/core/api/serverTypes';
import type { RivalSummary } from '@festival/core/api/serverTypes';
import { deriveComboFromSettings } from './helpers/comboUtils';
import RivalRow from './components/RivalRow';
import { Routes } from '../../routes';
import fx from '../../styles/effects.module.css';
import { useRivalsSharedStyles } from './useRivalsSharedStyles';
import Page from '../Page';
import { rivalsSlides } from './firstRun';
import LeaderboardRivalsTab from './LeaderboardRivalsTab';
import { ActionPill } from '../../components/common/ActionPill';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useModalState } from '../../hooks/ui/useModalState';
import RankByModal from '../leaderboards/modals/RankByModal';

// Module-level data cache so back-navigation has instant data
let _cachedInstrumentRivals: InstrumentRivals[] = [];
let _cachedComboRivals: RivalsListResponse | null = null;
let _cachedComputedAt: string | null = null;
let _cachedRivalsKey: string | null = null;

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
const QUICK_LINK_INSTRUMENT_ICON_SCALE = 1.15;

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

  const activeTab = (searchParams.get('tab') === 'leaderboard' ? 'leaderboard' : 'song') as 'song' | 'leaderboard';
  const rankBy = (searchParams.get('rankBy') as RankingMetric) || 'totalscore';
  const setTab = useCallback((tab: 'song' | 'leaderboard') => {
    const params: Record<string, string> = {};
    if (tab === 'leaderboard') { params.tab = 'leaderboard'; if (rankBy !== 'totalscore') params.rankBy = rankBy; }
    setSearchParams(params, { replace: true });
  }, [setSearchParams, rankBy]);
  const setRankBy = useCallback((metric: RankingMetric) => {
    const params: Record<string, string> = { tab: 'leaderboard' };
    if (metric !== 'totalscore') params.rankBy = metric;
    setSearchParams(params, { replace: true });
  }, [setSearchParams]);

  const metricModal = useModalState<RankingMetric>(() => 'totalscore');

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

  const activeInstruments = visibleInstruments(settings);
  const combo = useMemo(() => deriveComboFromSettings(settings), [settings]);
  const rivalsScopeKey = `${accountId ?? ''}:${activeInstruments.join(',')}:${combo ?? 'none'}`;
  const noRivalsSubtitle = useMemo(() => {
    if (activeInstruments.length === 0) return undefined;
    return t(activeInstruments.length === 1 ? 'rivals.noRivalsSubtitleSingle' : 'rivals.noRivalsSubtitlePlural');
  }, [activeInstruments.length, t]);

  // Data state: initialize from cache when returning to same account
  const hasCachedData = rivalsScopeKey === _cachedRivalsKey && _cachedInstrumentRivals.length > 0;

  const [instrumentRivals, setInstrumentRivals] = useState<InstrumentRivals[]>(hasCachedData ? _cachedInstrumentRivals : []);
  const [comboRivals, setComboRivals] = useState<RivalsListResponse | null>(hasCachedData ? _cachedComboRivals : null);
  const [comboLoading, setComboLoading] = useState(false);
  const [computedAt, setComputedAt] = useState<string | null>(hasCachedData ? _cachedComputedAt : null);
  const [, setPlayerName] = useState<string | null>(null);

  // Register toggle action for FAB and sync active tab
  const toggleTabRef = useRef(toggleTab);
  toggleTabRef.current = toggleTab;
  /* v8 ignore start — FAB registration */
  useEffect(() => {
    fabSearch.registerRivalsActions({ toggleTab: () => toggleTabRef.current() });
  }, [fabSearch]);
  useEffect(() => {
    fabSearch.setRivalsActiveTab(activeTab);
  }, [fabSearch, activeTab]);
  /* v8 ignore stop */

  // Resolve player display name
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId) return;
    if (player?.accountId === accountId) {
      setPlayerName(player.displayName);
      return;
    }
    let cancelled = false;
    api.getPlayer(accountId).then(res => {
      if (!cancelled) setPlayerName(res.displayName);
    }).catch(() => { /* ignored */ });
    return () => { cancelled = true; };
  }, [accountId, player]);
  /* v8 ignore stop */

  // Fetch overview for computedAt timestamp
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || hasCachedData) return;
    let cancelled = false;
    api.getRivalsOverview(accountId).then(res => {
      if (!cancelled) setComputedAt(res.computedAt);
    }).catch(() => { /* ignored */ });
    return () => { cancelled = true; };
  }, [accountId]);
  /* v8 ignore stop */

  // Fetch per-instrument rivals
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId) return;
    // Skip re-fetch on back-nav when cached data exists
    if (hasCachedData) return;
    let cancelled = false;

    const entries: InstrumentRivals[] = activeInstruments.map(inst => ({
      instrument: inst,
      data: null,
      loading: true,
      error: null,
    }));
    setInstrumentRivals(entries);

    activeInstruments.forEach((inst, idx) => {
      api.getRivalsList(accountId, inst).then(res => {
        if (cancelled) return;
        setInstrumentRivals(prev => {
          const next = [...prev];
          next[idx] = { instrument: inst, data: res, loading: false, error: null };
          return next;
        });
      }).catch(err => {
        if (cancelled) return;
        setInstrumentRivals(prev => {
          const next = [...prev];
          next[idx] = { instrument: inst, data: null, loading: false, error: err instanceof Error ? err.message : 'Error' };
          return next;
        });
      });
    });

    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- activeInstruments derived from settings
  }, [accountId, settings.showLead, settings.showBass, settings.showDrums, settings.showVocals, settings.showProLead, settings.showProBass, settings.showPeripheralVocals, settings.showPeripheralCymbals, settings.showPeripheralDrums]);
  /* v8 ignore stop */

  // Fetch combo rivals
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || !combo) {
      setComboRivals(null);
      setComboLoading(false);
      return;
    }
    // Skip re-fetch on back-nav when cached data exists
    if (hasCachedData) return;
    let cancelled = false;
    setComboLoading(true);

    api.getRivalsList(accountId, combo).then(res => {
      if (!cancelled) { setComboRivals(res); setComboLoading(false); }
    }).catch(() => {
      if (!cancelled) {
        setComboLoading(false);
      }
    });
    return () => { cancelled = true; };
  }, [accountId, combo]);
  /* v8 ignore stop */

  const allInstrumentsReady = instrumentRivals.length > 0 && instrumentRivals.every(r => !r.loading);
  const comboReady = !combo || !comboLoading;
  const allReady = allInstrumentsReady && comboReady;

  // Persist data to module-level cache for instant back-nav
  useEffect(() => {
    if (!allReady || !accountId) return;
    _cachedRivalsKey = rivalsScopeKey;
    _cachedInstrumentRivals = instrumentRivals;
    _cachedComboRivals = comboRivals;
    _cachedComputedAt = computedAt;
  }, [allReady, accountId, instrumentRivals, comboRivals, computedAt]);

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
  const { forDelay: stagger, next: nextStagger, clearAnim } = useStagger(shouldStagger);
  const shared = useRivalsSharedStyles();
  const styles = useMemo(() => ({
    ...shared,
  }), [shared]);

  /* v8 ignore start -- guard + computed state */
  if (!accountId) {
    return <div style={styles.center}>{t('rivals.noPlayer')}</div>;
  }
  /* v8 ignore stop */

  /* v8 ignore start -- render-time helpers */
  /** Compute CSS variable for min name width based on longest name in a rival list. */
  const nameWidthVar = (rivals: RivalSummary[]): React.CSSProperties => {
    const maxLen = rivals.reduce((max, r) => Math.max(max, (r.displayName ?? 'Unknown Player').length), 0);
    return { '--rival-name-width': `${Math.ceil(maxLen * 0.85)}ch` } as React.CSSProperties;
  };

  const navigateToRival = (rivalId: string, rivalName?: string | null) => {
    navigate(Routes.rivalDetail(rivalId, rivalName ?? undefined), { state: { combo, rivalName } });
  };
  /* v8 ignore stop */

  const PREVIEW_COUNT = 3;

  /* v8 ignore start -- computed render state */
  const hasAnyRivals = instrumentRivals.some(r => r.data && (r.data.above.length > 0 || r.data.below.length > 0))
    || (comboRivals && (comboRivals.above.length > 0 || comboRivals.below.length > 0))
    || (commonRivals.above.length > 0 || commonRivals.below.length > 0);
  /* v8 ignore stop */

  const quickLinkItems = useMemo<RivalQuickLink[]>(() => {
    if (activeTab !== 'song') {
      return [];
    }

    const links: RivalQuickLink[] = [];

    if (commonRivals.above.length > 0 || commonRivals.below.length > 0) {
      const commonLabel = t('rivals.commonRivalsShort', 'Common Rivals');
      links.push({
        id: 'common',
        label: commonLabel,
        landmarkLabel: commonLabel,
      });
    }

    if (combo && comboRivals && (comboRivals.above.length > 0 || comboRivals.below.length > 0)) {
      const comboLabel = t('rivals.instrumentRivalsShort', { instrument: t('rivals.combo') });
      links.push({
        id: 'combo',
        label: comboLabel,
        landmarkLabel: comboLabel,
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
            style={{
              transform: `scale(${QUICK_LINK_INSTRUMENT_ICON_SCALE})`,
              transformOrigin: 'center',
            }}
          />
        ),
      });
    }

    return links;
  }, [activeTab, combo, comboRivals, commonRivals.above.length, commonRivals.below.length, instrumentRivals, t]);

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
    scrollOffset: Gap.md,
  });

  const handleModalQuickLinkSelect = useCallback((link: RivalQuickLink) => {
    closeQuickLinks();
    handleQuickLinkSelect(link);
  }, [closeQuickLinks, handleQuickLinkSelect]);

  const pageQuickLinks = useMemo<PageQuickLinksConfig | undefined>(() => {
    if (activeTab !== 'song' || phase !== LoadPhase.ContentIn || quickLinkItems.length < 2) {
      return undefined;
    }

    return {
      title: t('rivals.quickLinks'),
      items: quickLinkItems,
      activeItemId,
      visible: quickLinksOpen,
      onOpen: openQuickLinks,
      onClose: closeQuickLinks,
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
  }, [activeItemId, activeTab, closeQuickLinks, handleModalQuickLinkSelect, handleQuickLinkSelect, isWideDesktop, openQuickLinks, phase, quickLinkItems, quickLinksOpen, t]);

  const compactQuickLinksAction = !isWideDesktop && pageQuickLinks
    ? (
      <ActionPill
        icon={<IoCompass size={Size.iconAction} />}
        label={t('rivals.quickLinks')}
        onClick={openQuickLinks}
      />
    )
    : null;

  /* v8 ignore start -- JSX render tree */
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: true }), []);

  return (
    <Page
      scrollRestoreKey={`rivals:${accountId}`}
      scrollDeps={[phase]}
      loadPhase={phase}
      containerStyle={styles.container}
      quickLinks={pageQuickLinks}
      before={
        isMobile ? undefined : (
          <PageHeader
            title={activeTab === 'song' ? t('rivals.tabSong') : t('rivals.tabLeaderboard')}
            actions={phase === LoadPhase.ContentIn ? (
              <>
                <ActionPill
                  icon={activeTab === 'song' ? <IoTrophy size={Size.iconAction} /> : <IoMusicalNotes size={Size.iconAction} />}
                  label={activeTab === 'song' ? t('rivals.tabLeaderboard') : t('rivals.tabSong')}
                  onClick={toggleTab}
                />
                {compactQuickLinksAction}
                {activeTab === 'leaderboard' && settings.enableExperimentalRanks && (
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
        )
      }
      firstRun={{ key: 'rivals', label: t('rivals.title'), slides: rivalsSlides, gateContext: firstRunGateCtx }}
      fabSpacer={phase === LoadPhase.ContentIn && !hasAnyRivals ? 'none' : 'end'}
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
                  <div
                    className={fx.sectionHeaderClickable}
                    style={{ ...styles.sectionHeaderClickable, ...nextStagger() }}
                    onAnimationEnd={clearAnim}
                    onClick={navigateToCommon}
                    role="button"
                    tabIndex={0}
                    onKeyDown={e => { if (e.key === 'Enter') navigateToCommon(); }}
                  >
                    <div style={styles.cardHeaderText}>
                      <span style={styles.cardTitle}>{t('rivals.commonRivalsShort', 'Common Rivals')}</span>
                    </div>
                    <span style={styles.seeAll}>{t('rivals.seeAll', 'See All')}</span>
                    <IoChevronForward size={20} style={styles.chevron} />
                  </div>
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
                    <div style={{ ...styles.viewAllButton, ...nextStagger() }} onAnimationEnd={clearAnim} onClick={navigateToCommon}>
                      {t('rivals.viewAllRivals')}
                    </div>
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
                  <div
                    className={fx.sectionHeaderClickable}
                    style={{ ...styles.sectionHeaderClickable, ...nextStagger() }}
                    onAnimationEnd={clearAnim}
                    onClick={navigateToCombo}
                    role="button"
                    tabIndex={0}
                    onKeyDown={e => { if (e.key === 'Enter') navigateToCombo(); }}
                  >
                    <div style={styles.cardHeaderText}>
                      <span style={styles.cardTitle}>{t('rivals.instrumentRivalsShort', { instrument: t('rivals.combo') })}</span>
                    </div>
                    <span style={styles.seeAll}>{t('rivals.seeAll', 'See All')}</span>
                    <IoChevronForward size={20} style={styles.chevron} />
                  </div>
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
                    <div style={{ ...styles.viewAllButton, ...nextStagger() }} onAnimationEnd={clearAnim} onClick={navigateToCombo}>
                      {t('rivals.viewAllRivals')}
                    </div>
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
                    <div
                      className={fx.sectionHeaderClickable}
                      style={{ ...styles.sectionHeaderClickable, ...nextStagger() }}
                      onAnimationEnd={clearAnim}
                      onClick={navigateToInstrument}
                      role="button"
                      tabIndex={0}
                      onKeyDown={e => { if (e.key === 'Enter') navigateToInstrument(); }}
                    >
                      <InstrumentHeader instrument={entry.instrument} size={InstrumentHeaderSize.SM} iconOnly />
                      <div style={styles.cardHeaderText}>
                        <span style={styles.cardTitle}>{t('rivals.instrumentRivalsShort', { instrument: serverInstrumentLabel(entry.instrument) })}</span>
                      </div>
                      <span style={styles.seeAll}>{t('rivals.seeAll', 'See All')}</span>
                      <IoChevronForward size={20} style={styles.chevron} />
                    </div>
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
                      <div style={{ ...styles.viewAllButton, ...nextStagger() }} onAnimationEnd={clearAnim} onClick={navigateToInstrument}>
                        {t('rivals.viewAllRivals')}
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
              ) : (
                <LeaderboardRivalsTab accountId={accountId} shouldStagger={shouldStagger} rankBy={rankBy} />
              )}
            </>
      )}
    </Page>
  );
}

/* v8 ignore stop */
