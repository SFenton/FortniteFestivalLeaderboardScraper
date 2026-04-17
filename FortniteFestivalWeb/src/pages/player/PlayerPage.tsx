/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useNavigationType } from 'react-router-dom';
import { useFestival } from '../../contexts/FestivalContext';
import { usePlayerData } from '../../contexts/PlayerDataContext';
import { useSyncStatus } from '../../hooks/data/useSyncStatus';
import { useSettings, isInstrumentVisible } from '../../contexts/SettingsContext';
import { api } from '../../api/client';
import ArcSpinner from '../../components/common/ArcSpinner';
import EmptyState from '../../components/common/EmptyState';
import { parseApiError } from '../../utils/apiError';
import { buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import { LoadPhase } from '@festival/core';
import { SERVER_INSTRUMENT_KEYS as INSTRUMENT_KEYS, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { fixedFill, flexCenter, ZIndex, SPINNER_FADE_MS, CONTENT_OUT_MS } from '@festival/theme';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useQuery, useQueries, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '../../api/queryKeys';
import PlayerContent from '../leaderboard/player/components/PlayerContent';
import { useRankHistoryAll } from '../../hooks/chart/useRankHistory';
import { useRegisterFirstRun } from '../../hooks/ui/useRegisterFirstRun';
import { useFirstRun } from '../../hooks/ui/useFirstRun';
import FirstRunCarousel from '../../components/firstRun/FirstRunCarousel';
import { statisticsSlides } from './firstRun';


/** Track rendered accounts so we can skip stagger animation on revisit. */
let _renderedPlayerAccount: string | null = null;
let _renderedTrackedAccount: string | null = null;

export function clearPlayerPageCache() {
  _renderedPlayerAccount = null;
  _renderedTrackedAccount = null;
}

export default function PlayerPage({ accountId: propAccountId }: { accountId?: string } = {}) {
  const { t } = useTranslation();
  const params = useParams<{ accountId: string }>();
  const accountId = propAccountId ?? params.accountId;
  useNavigationType();
  const {
    state: { songs },
  } = useFestival();

  // Use cached context data when viewing the tracked player (statistics tab)
  const ctx = usePlayerData();
  const isTrackedPlayer = !!propAccountId;

  // First-run carousel
  const isMobileChrome = useIsMobileChrome();
  const slidesMemo = useMemo(() => statisticsSlides(isMobileChrome), [isMobileChrome]);
  useRegisterFirstRun('statistics', t('nav.statistics'), slidesMemo);
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: true }), []);
  const firstRun = useFirstRun('statistics', firstRunGateCtx);

  // Local state for when viewing an arbitrary player via URL -- use React Query
  const { data: queryData, isLoading: queryLoading, error: queryError } = useQuery({
    queryKey: queryKeys.player(accountId ?? ''),
    queryFn: () => api.getPlayer(accountId!),
    enabled: !!accountId && !isTrackedPlayer,
  });
  const qc = useQueryClient();

  const { isSyncing: localSyncing, phase: localPhase, backfillProgress: localBfProg, historyProgress: localHrProg, rivalsProgress: localRvProg, entriesFound: localEntriesFound, itemsCompleted: localItemsCompleted, totalItems: localTotalItems, currentSongName: localCurrentSong, seasonsQueried: localSeasonsQueried, rivalsFound: localRivalsFound, isThrottled: localIsThrottled, throttleStatusKey: localThrottleStatusKey, pendingRankUpdate: localPendingRankUpdate, estimatedRankUpdateMinutes: localEstimatedRankUpdateMinutes, probeStatusKey: localProbeStatusKey, nextRetrySeconds: localNextRetrySeconds, justCompleted: localJustCompleted, clearCompleted: localClearCompleted } =
    useSyncStatus(!isTrackedPlayer ? accountId : undefined, { track: false });

  // For tracked player, pull justCompleted from context
  const justCompleted = isTrackedPlayer ? ctx.justCompleted : localJustCompleted;
  const clearCompleted = isTrackedPlayer ? ctx.clearCompleted : localClearCompleted;

  // Track whether we're showing the completion banner (auto-dismisses after 3s)
  // For tracked player, also check shared dismissal state
  const [showCompleteBanner, setShowCompleteBanner] = useState(false);

  // When sync completes, trigger content fade-out then re-stagger with new data
  const triggerContentOutRef = useRef<() => void>(() => {});

  useEffect(() => {
    if (!justCompleted || !accountId) return;
    clearCompleted();
    if (!(isTrackedPlayer && ctx.syncBannerDismissed)) {
      setShowCompleteBanner(true);
    }

    // Reset rendered cache so re-stagger plays with fresh animation
    if (isTrackedPlayer) _renderedTrackedAccount = null;
    else _renderedPlayerAccount = null;
    skipAnimRef.current = false;

    // Trigger content fade-out; after it completes, useLoadPhase transitions
    // to Loading, then we invalidate queries so fresh data triggers re-stagger
    triggerContentOutRef.current();
  }, [justCompleted, clearCompleted, accountId, isTrackedPlayer, ctx.syncBannerDismissed]);

  // Hide local banner when dismissed globally from another page (tracked player only)
  useEffect(() => {
    if (isTrackedPlayer && ctx.syncBannerDismissed) setShowCompleteBanner(false);
  }, [isTrackedPlayer, ctx.syncBannerDismissed]);

  // Resolve effective values: context for tracked player, query for others
  const data = isTrackedPlayer ? ctx.playerData : (queryData ?? null);
  const loading = isTrackedPlayer ? ctx.playerLoading : queryLoading;
  const error = isTrackedPlayer ? ctx.playerError : (queryError ? (queryError instanceof Error ? queryError.message : t('player.failedToLoad')) : null);
  const isSyncing = isTrackedPlayer ? ctx.isSyncing : localSyncing;
  const phase = isTrackedPlayer ? ctx.syncPhase : localPhase;
  const backfillProgress = isTrackedPlayer ? ctx.backfillProgress : localBfProg;
  const historyProgress = isTrackedPlayer ? ctx.historyProgress : localHrProg;
  const rivalsProgress = isTrackedPlayer ? ctx.rivalsProgress : localRvProg;
  const entriesFound = isTrackedPlayer ? ctx.entriesFound : localEntriesFound;
  const itemsCompleted = isTrackedPlayer ? ctx.itemsCompleted : localItemsCompleted;
  const totalItems = isTrackedPlayer ? ctx.totalItems : localTotalItems;
  const currentSongName = isTrackedPlayer ? ctx.currentSongName : localCurrentSong;
  const seasonsQueried = isTrackedPlayer ? ctx.seasonsQueried : localSeasonsQueried;
  const rivalsFound = isTrackedPlayer ? ctx.rivalsFound : localRivalsFound;
  const isThrottled = isTrackedPlayer ? ctx.isThrottled : localIsThrottled;
  const throttleStatusKey = isTrackedPlayer ? ctx.throttleStatusKey : localThrottleStatusKey;
  const pendingRankUpdate = isTrackedPlayer ? ctx.pendingRankUpdate : localPendingRankUpdate;
  const estimatedRankUpdateMinutes = isTrackedPlayer ? ctx.estimatedRankUpdateMinutes : localEstimatedRankUpdateMinutes;
  const probeStatusKey = isTrackedPlayer ? ctx.probeStatusKey : localProbeStatusKey;
  const nextRetrySeconds = isTrackedPlayer ? ctx.nextRetrySeconds : localNextRetrySeconds;

  // Compute visible instruments for ranking queries
  const { settings } = useSettings();
  const visibleKeys = useMemo(() =>
    INSTRUMENT_KEYS.filter(k => isInstrumentVisible(settings, k)),
    [settings],
  );

  // Fetch pre-computed tiered stats (hoisted from PlayerContent so stagger waits for data)
  const statsQuery = useQuery({
    queryKey: queryKeys.playerStats(accountId ?? ''),
    queryFn: () => api.getPlayerStats(accountId!),
    staleTime: 5 * 60_000,
    enabled: !!accountId,
  });
  const statsData = statsQuery.data ?? null;
  const hasRankTiers = !!statsData?.instrumentRanks?.length;

  // Fetch per-instrument rankings (only when stats don't already provide rank tiers)
  const instrumentRankingQueries = useQueries({
    queries: accountId
      ? visibleKeys.map((inst) => ({
          queryKey: queryKeys.playerRanking(inst, accountId),
          queryFn: () => api.getPlayerRanking(inst, accountId),
          staleTime: 5 * 60_000,
          enabled: !hasRankTiers,
        }))
      : [],
  });

  // Hoist rank-history loading so stagger waits for chart data (same pattern as LeaderboardsOverviewPage)
  const allHistory = useRankHistoryAll(visibleKeys, accountId, 'totalscore');
  const historyLoading = accountId ? visibleKeys.some(inst => allHistory[inst]?.loading) : false;
  const historyAllCached = !accountId || visibleKeys.every(inst => allHistory[inst]?.chartData != null && !allHistory[inst]?.loading);

  // Skip stagger if we've rendered this account before.
  const hasRendered = isTrackedPlayer
    ? _renderedTrackedAccount === accountId
    : _renderedPlayerAccount === accountId;
  const prevAccountRef = useRef(accountId);
  const skipAnimRef = useRef(hasRendered);
  // When accountId changes within the same component instance, re-evaluate skip
  /* v8 ignore start -- animation: skip-stagger state for re-renders */
  if (prevAccountRef.current !== accountId) {
    prevAccountRef.current = accountId;
    const alreadyRendered = isTrackedPlayer
      ? _renderedTrackedAccount === accountId
      : _renderedPlayerAccount === accountId;
    skipAnimRef.current = alreadyRendered;
  }
  /* v8 ignore stop */
  const skipAnim = skipAnimRef.current;
  const statsReady = !statsQuery.isLoading;
  const rankingsReady = hasRankTiers || instrumentRankingQueries.every(q => !q.isLoading);
  const dataReady = !loading && !error && !!data && statsReady && rankingsReady && !historyLoading;
  const allCached = !!data && statsQuery.data != null
    && (hasRankTiers || instrumentRankingQueries.every(q => q.data != null))
    && historyAllCached;
  const { phase: loadPhase, triggerContentOut } = useLoadPhase(dataReady, { skipAnimation: skipAnim || allCached });
  triggerContentOutRef.current = triggerContentOut;

  // When content-out finishes and phase reaches Loading, invalidate queries so fresh data arrives
  const prevLoadPhase = useRef(loadPhase);
  useEffect(() => {
    if (prevLoadPhase.current === LoadPhase.ContentOut && loadPhase === LoadPhase.Loading && accountId) {
      void qc.invalidateQueries({ queryKey: queryKeys.player(accountId) });
      void qc.invalidateQueries({ queryKey: queryKeys.playerStats(accountId) });
      for (const inst of visibleKeys) {
        void qc.invalidateQueries({ queryKey: queryKeys.playerRanking(inst, accountId) });
        void qc.invalidateQueries({ queryKey: queryKeys.rankHistory(inst, accountId, 30) });
      }
    }
    prevLoadPhase.current = loadPhase;
  }, [loadPhase, accountId, isTrackedPlayer, qc, visibleKeys]);

  if (data) {
    if (isTrackedPlayer) _renderedTrackedAccount = accountId!;
    else _renderedPlayerAccount = accountId!;
  }

  const styles = useStyles();

  if (error) {
    const parsed = parseApiError(error);
    return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
  }
  if (!loading && !data) return <div style={styles.center}>{t('player.playerNotFound')}</div>;

  return (
    <>
      {loadPhase !== LoadPhase.ContentIn && loadPhase !== LoadPhase.ContentOut && (
        <div
          style={loadPhase === LoadPhase.SpinnerOut ? styles.centerFadeOut : styles.center}
        >
          <ArcSpinner />
        </div>
      )}
      {loadPhase === LoadPhase.ContentOut && data && (
        <div style={styles.contentOut}>
          <PlayerContent key={`${accountId}-out`} data={data} songs={songs} isSyncing={false} phase={phase} backfillProgress={backfillProgress} historyProgress={historyProgress} rivalsProgress={rivalsProgress} itemsCompleted={itemsCompleted} totalItems={totalItems} entriesFound={entriesFound} currentSongName={currentSongName} seasonsQueried={seasonsQueried} rivalsFound={rivalsFound} isThrottled={isThrottled} throttleStatusKey={throttleStatusKey} probeStatusKey={probeStatusKey} nextRetrySeconds={nextRetrySeconds} pendingRankUpdate={pendingRankUpdate} estimatedRankUpdateMinutes={estimatedRankUpdateMinutes} isTrackedPlayer={isTrackedPlayer} skipAnim={true} statsData={statsData} rankingQueryResults={instrumentRankingQueries.map(q => q.data ?? null)} />
        </div>
      )}
      {loadPhase === LoadPhase.ContentIn && data && (
        <PlayerContent key={accountId} data={data} songs={songs} isSyncing={isSyncing} phase={phase} backfillProgress={backfillProgress} historyProgress={historyProgress} rivalsProgress={rivalsProgress} itemsCompleted={itemsCompleted} totalItems={totalItems} entriesFound={entriesFound} currentSongName={currentSongName} seasonsQueried={seasonsQueried} rivalsFound={rivalsFound} isThrottled={isThrottled} throttleStatusKey={throttleStatusKey} probeStatusKey={probeStatusKey} nextRetrySeconds={nextRetrySeconds} pendingRankUpdate={pendingRankUpdate} estimatedRankUpdateMinutes={estimatedRankUpdateMinutes} isTrackedPlayer={isTrackedPlayer} skipAnim={skipAnim} showCompleteBanner={showCompleteBanner} onCompleteBannerDismissed={() => { setShowCompleteBanner(false); if (isTrackedPlayer) ctx.dismissSyncBanner(); }} statsData={statsData} rankingQueryResults={instrumentRankingQueries.map(q => q.data ?? null)} />
      )}
      {firstRun.show && <FirstRunCarousel slides={firstRun.slides} onDismiss={firstRun.dismiss} onExitComplete={firstRun.onExitComplete} />}
    </>
  );
}

function useStyles() {
  return useMemo(() => ({
    center: {
      ...fixedFill,
      zIndex: ZIndex.dropdown,
      ...flexCenter,
    } as CSSProperties,
    centerFadeOut: {
      ...fixedFill,
      zIndex: ZIndex.dropdown,
      ...flexCenter,
      animation: `fadeOut ${SPINNER_FADE_MS}ms ease-out forwards`,
    } as CSSProperties,
    contentOut: {
      animation: `fadeOut ${CONTENT_OUT_MS}ms ease-out forwards`,
    } as CSSProperties,
  }), []);
}