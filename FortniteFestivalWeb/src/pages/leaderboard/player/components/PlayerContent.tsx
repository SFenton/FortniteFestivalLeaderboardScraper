/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useRef, useState, useCallback, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useLocation } from 'react-router-dom';
import { IoCompass, IoMusicalNotes, IoPeople, IoStatsChart } from 'react-icons/io5';
import {
  computeOverallStats,
  groupByInstrument,
  findStatsTier,
  getInstrumentTiers,
  tierToInstrumentStats,
  computeInstrumentStats,
  resolveInstrumentRanks,
} from '../../../player/helpers/playerStats';
import { comboIdFromInstruments } from '@festival/core';
import { SERVER_INSTRUMENT_KEYS as INSTRUMENT_KEYS, serverInstrumentLabel, type ServerInstrumentKey as InstrumentKey, type PlayerResponse, type ServerSong as Song } from '@festival/core/api/serverTypes';
import { Align, Display, Gap, IconSize, Justify, Layout, Overflow, Radius, TRANSITION_MS, FADE_DURATION, frostedCard, transition, transitions, STAGGER_ENTRY_OFFSET, QUERY_NARROW_GRID } from '@festival/theme';
import { playerPageStyles as pps } from '../../../../components/player/playerPageStyles';
import { SelectProfilePill } from '../../../../components/player/SelectProfilePill';
import SyncBanner from '../../../../components/page/SyncBanner';
import SyncCompleteBanner from '../../../../components/page/SyncCompleteBanner';
import CollapseOnExit from '../../../../components/page/CollapseOnExit';
import { useSettings, isInstrumentVisible } from '../../../../contexts/SettingsContext';
import { loadSongSettings, saveSongSettings } from '../../../../utils/songSettings';
import Page from '../../../Page';
import PageHeader from '../../../../components/common/PageHeader';
import { ActionPill } from '../../../../components/common/ActionPill';
import { useIsMobile, useIsWideDesktop } from '../../../../hooks/ui/useIsMobile';
import { useMediaQuery } from '../../../../hooks/ui/useMediaQuery';
import { useTrackedPlayer } from '../../../../hooks/data/useTrackedPlayer';
import { useScoreFilter } from '../../../../hooks/data/useScoreFilter';
import { usePlayerPageSelect } from '../../../../contexts/FabSearchContext';
import { useSearchQuery } from '../../../../contexts/SearchQueryContext';
import { useScrollContainer } from '../../../../contexts/ScrollContainerContext';
import { useScrollFade } from '../../../../hooks/ui/useScrollFade';
import { getPageQuickLinkTestId, usePageQuickLinks, type PageQuickLinkItem } from '../../../../hooks/ui/usePageQuickLinks';
import { staggerCompletionDelay } from '../../../../hooks/ui/useStagger';
import ConfirmAlert from '../../../../components/modals/ConfirmAlert';
import FadeIn from '../../../../components/page/FadeIn';
import PageHeaderActionsTransition from '../../../../components/common/PageHeaderActionsTransition';
import PlayerSectionHeading from '../../../player/sections/PlayerSectionHeading';
import { buildOverallSummaryItems } from '../../../player/sections/OverallSummarySection';
import { buildInstrumentStatsItems } from '../../../player/sections/InstrumentStatsSection';
import { getLeaderboardPageForRank } from '../../../leaderboards/helpers/rankingHelpers';
import { buildTopSongsItems } from '../../../player/components/TopSongsSection';
import { buildPlayerBandsItems, EMPTY_PLAYER_BANDS } from '../../../player/components/PlayerBandsSection';
import type { PlayerItem } from '../../../player/helpers/playerPageTypes';
import type { SyncPhase } from '../../../../hooks/data/useSyncStatus';
import { Routes } from '../../../../routes';
import type { AccountRankingEntry, RankingMetric, InstrumentRankEntry, AccountRankingDto, PlayerStatsResponse } from '@festival/core/api/serverTypes';
import { useFeatureFlags } from '../../../../contexts/FeatureFlagsContext';
import { InstrumentIcon } from '../../../../components/display/InstrumentIcons';
import type { PageQuickLinksConfig } from '../../../../components/page/PageQuickLinks';
import { createPreserveShellScrollState } from '../../../../utils/quietNavigation';

type PlayerQuickLinkId = 'global' | 'top-songs' | 'bands' | `instrument:${InstrumentKey}`;

interface PlayerQuickLink extends PageQuickLinkItem {
  id: PlayerQuickLinkId;
  itemKey: string;
}

const QUICK_LINK_GLYPH_ICON_SIZE = 20;
const QUICK_LINK_INSTRUMENT_ICON_SCALE = 1.15;
const QUICK_LINK_SCROLL_OFFSET = Gap.md;
const QUICK_LINK_SCROLL_COMPLETE_THRESHOLD = 2;
const QUICK_LINK_SCROLL_SETTLE_DELAY_MS = 80;
const QUICK_LINK_PILL_ICON_SIZE = IconSize.action;
const SELECT_PROFILE_ACTION_SLOT_DESKTOP_MAX_WIDTH = 360;
let pendingSelectProfileExit: { accountId: string; until: number } | null = null;

const PLAYER_HEADER_ACTIONS_STYLE: CSSProperties = {
  display: Display.flex,
  alignItems: Align.center,
  justifyContent: Justify.end,
  minWidth: 0,
  transition: transition('gap', TRANSITION_MS),
};

const SELECT_PROFILE_ACTION_SLOT_STYLE: CSSProperties = {
  display: Display.flex,
  alignItems: Align.center,
  justifyContent: Justify.end,
  overflow: Overflow.hidden,
  flexShrink: 0,
  minWidth: 0,
  transition: transitions(
    transition('max-width', TRANSITION_MS),
    transition('opacity', TRANSITION_MS),
  ),
};

function primeSelectProfileExit(accountId: string) {
  pendingSelectProfileExit = {
    accountId,
    until: Date.now() + TRANSITION_MS,
  };
}

function getPendingSelectProfileExitDelay(accountId: string): number {
  if (!pendingSelectProfileExit || pendingSelectProfileExit.accountId !== accountId) {
    return 0;
  }

  const remaining = pendingSelectProfileExit.until - Date.now();
  if (remaining <= 0) {
    pendingSelectProfileExit = null;
    return 0;
  }

  return remaining;
}

function clearPendingSelectProfileExit(accountId: string) {
  if (pendingSelectProfileExit?.accountId === accountId) {
    pendingSelectProfileExit = null;
  }
}

function estimateVisiblePlayerGridItemCount(items: PlayerItem[]): number {
  const vh = typeof window !== 'undefined' ? window.innerHeight : 900;
  const gap = Gap.md;
  let accHeight = 0;
  let col = 0;
  let rowMax = 0;
  let visibleCount = items.length;

  for (let i = 0; i < items.length; i++) {
    const item = items[i]!;
    if (item.span) {
      if (col === 1) {
        accHeight += rowMax + gap;
        col = 0;
        rowMax = 0;
      }
      accHeight += item.heightEstimate + gap;
    } else {
      rowMax = Math.max(rowMax, item.heightEstimate);
      col++;
      if (col === 2) {
        accHeight += rowMax + gap;
        col = 0;
        rowMax = 0;
      }
    }

    if (accHeight > vh && visibleCount === items.length) {
      visibleCount = i + 2;
    }
  }

  return visibleCount;
}

export interface PlayerContentProps {
  data: PlayerResponse;
  songs: Song[];
  isSyncing: boolean;
  phase: SyncPhase;
  backfillProgress: number;
  historyProgress: number;
  rivalsProgress: number;
  itemsCompleted: number;
  totalItems: number;
  entriesFound: number;
  currentSongName: string | null;
  seasonsQueried: number;
  rivalsFound: number;
  isThrottled: boolean;
  throttleStatusKey: string | null;
  probeStatusKey: string | null;
  nextRetrySeconds: number | null;
  pendingRankUpdate: boolean;
  estimatedRankUpdateMinutes: number | null;
  isTrackedPlayer: boolean;
  skipAnim: boolean;
  showCompleteBanner?: boolean;
  onCompleteBannerDismissed?: () => void;
  statsData: PlayerStatsResponse | null;
  rankingQueryResults: (AccountRankingDto | null)[];
}

export default function PlayerContent({
  data,
  songs,
  isSyncing,
  phase: syncPhase,
  backfillProgress,
  historyProgress,
  rivalsProgress,
  itemsCompleted,
  totalItems,
  entriesFound,
  currentSongName,
  seasonsQueried,
  rivalsFound,
  isThrottled,
  throttleStatusKey,
  probeStatusKey,
  nextRetrySeconds,
  pendingRankUpdate,
  estimatedRankUpdateMinutes,
  isTrackedPlayer,
  skipAnim,
  showCompleteBanner,
  onCompleteBannerDismissed,
  statsData,
  rankingQueryResults,
}: PlayerContentProps) {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const { leaderboards: leaderboardsEnabled, playerBands: playerBandsEnabled } = useFeatureFlags();
  const scrollContainerRef = useScrollContainer();
  const location = useLocation();
  const navigate = useNavigate();
  const { player: trackedPlayer, setPlayer } = useTrackedPlayer();
  const primeTrackedPlayerSelection = useCallback(() => {
    primeSelectProfileExit(data.accountId);
    setPlayer({ accountId: data.accountId, displayName: data.displayName });
    if (location.pathname !== Routes.statistics) {
      navigate(Routes.statistics, {
        replace: true,
        state: createPreserveShellScrollState(`profile-select:${data.accountId}`),
      });
    }
  }, [data.accountId, data.displayName, location.pathname, navigate, setPlayer]);
  const [pendingSwitch, setPendingSwitch] = useState<(() => void) | null>(null);
  const bannerVisible = isSyncing || !!(showCompleteBanner && onCompleteBannerDismissed);
  const [bannerCollapsed, setBannerCollapsed] = useState(!bannerVisible);
  useEffect(() => { if (bannerVisible) setBannerCollapsed(false); }, [bannerVisible]);
  const { filterPlayerScores, isScoreValid, enabled: filterInvalidScores, leeway } = useScoreFilter();
  const { registerPlayerPageSelect } = usePlayerPageSelect();
  const pendingSelectProfileExitTimerRef = useRef<number | null>(null);
  const gridListRef = useRef<HTMLDivElement>(null);

  const clearPendingSelectProfileExitTimer = useCallback(() => {
    if (pendingSelectProfileExitTimerRef.current === null) return;
    window.clearTimeout(pendingSelectProfileExitTimerRef.current);
    pendingSelectProfileExitTimerRef.current = null;
  }, []);

  useEffect(() => () => {
    clearPendingSelectProfileExitTimer();
  }, [clearPendingSelectProfileExitTimer]);

  // Register FAB "Select as Profile" action
  useEffect(() => {
    if (trackedPlayer?.accountId === data.accountId) {
      /* v8 ignore start */
      registerPlayerPageSelect(null);
      return;
      /* v8 ignore stop */
    }
    registerPlayerPageSelect({
      displayName: data.displayName,
      /* v8 ignore start — profile switch callbacks */
      onSelect: () => {
        if (trackedPlayer && trackedPlayer.accountId !== data.accountId) {
          setPendingSwitch(() => primeTrackedPlayerSelection);
        } else {
          primeTrackedPlayerSelection();
        /* v8 ignore stop */
        }
      },
    });
    return () => registerPlayerPageSelect(null);
  }, [data.accountId, data.displayName, trackedPlayer, primeTrackedPlayerSelection, registerPlayerPageSelect]);

  // Helper: wrap a navigation action with profile-switch logic when viewing another player
  /* v8 ignore start — navigation + profile switch */
  const withProfileSwitch = useCallback((action: () => void) => {
    if (!isTrackedPlayer) {
      const selectAndGo = () => {
        setPlayer({ accountId: data.accountId, displayName: data.displayName });
        action();
      };
      if (trackedPlayer && trackedPlayer.accountId !== data.accountId) {
        setPendingSwitch(() => selectAndGo);
      } else { selectAndGo(); }
    } else { action(); }
    /* v8 ignore stop */
  }, [isTrackedPlayer, trackedPlayer, data.accountId, data.displayName, setPlayer]);

  const effectiveScores = useMemo(() => {
    const visible = data.scores.filter(s => isInstrumentVisible(settings, s.instrument as InstrumentKey));
    return filterPlayerScores(visible);
  }, [data.scores, settings, filterPlayerScores],
  );
  const visibleKeys = useMemo(() =>
    INSTRUMENT_KEYS.filter(k => isInstrumentVisible(settings, k)),
    [settings],
  );

  // Effective leeway for tier selection: when filtering disabled, pick the last (all-inclusive) tier
  const effectiveLeeway = filterInvalidScores ? leeway : Infinity;

  // Use rank tiers from stats response when available; fall back to per-instrument ranking results
  const hasRankTiers = !!statsData?.instrumentRanks?.length;

  // Build map of instrument → AccountRankingEntry for passing to sections
  const instrumentRankings = useMemo(() => {
    const map = new Map<InstrumentKey, AccountRankingEntry>();

    // If rank tiers available from stats, resolve ranks at current leeway
    if (statsData?.instrumentRanks?.length) {
      for (const entry of statsData.instrumentRanks as InstrumentRankEntry[]) {
        const inst = visibleKeys.find(k => entry.ins === comboIdFromInstruments([k]));
        if (!inst) continue;
        const resolved = resolveInstrumentRanks(entry, effectiveLeeway);
        // Build a minimal AccountRankingEntry with rank fields
        map.set(inst, {
          accountId: data.accountId,
          adjustedSkillRank: resolved.adjusted,
          weightedRank: resolved.weighted,
          fcRateRank: resolved.fcRate,
          totalScoreRank: resolved.totalScore,
          maxScorePercentRank: resolved.maxScore,
          // Non-rank fields default — only ranks used in stat cards
          songsPlayed: 0, totalChartedSongs: 0, coverage: 0,
          rawSkillRating: 0, adjustedSkillRating: 0,
          weightedRating: 0, fcRate: 0, totalScore: 0,
          maxScorePercent: 0, avgAccuracy: 0, fullComboCount: 0,
          avgStars: 0, bestRank: 0, avgRank: 0,
          rawMaxScorePercent: null, computedAt: '',
        } as AccountRankingEntry);
      }
      return map;
    }

    // Fallback to ranking query results passed from parent
    for (let i = 0; i < visibleKeys.length; i++) {
      const entry = rankingQueryResults[i];
      if (entry) map.set(visibleKeys[i]!, entry);
    }
    return map;
  }, [visibleKeys, rankingQueryResults, statsData, effectiveLeeway, hasRankTiers, data.accountId]);

  const songMap = useMemo(() => new Map(songs.map((s) => [s.songId, s])), [songs]);
  const byInstrument = useMemo(() => groupByInstrument(effectiveScores), [effectiveScores]);
  const rawByInstrument = useMemo(() => {
    const visible = data.scores.filter(s => isInstrumentVisible(settings, s.instrument as InstrumentKey));
    return groupByInstrument(visible);
  }, [data.scores, settings]);
  const overallStats = useMemo(() => computeOverallStats(effectiveScores), [effectiveScores]);

  const searchQuery = useSearchQuery();

  // Stable navigation helpers to reduce closure overhead in onClick handlers
  /* v8 ignore start — navigation helpers */
  const navigateToSongs = useCallback((settingsUpdater: (s: ReturnType<typeof loadSongSettings>) => ReturnType<typeof loadSongSettings>) => {
    withProfileSwitch(() => {
      const s = loadSongSettings();
      saveSongSettings(settingsUpdater(s));
      searchQuery.setQuery('');
      navigate('/songs', { state: { backTo: location.pathname, restagger: true } });
    /* v8 ignore stop */
    });
  }, [withProfileSwitch, navigate, location.pathname, searchQuery]);

  /* v8 ignore start — navigation helper */
  const navigateToSongDetail = useCallback((songId: string, instrument: InstrumentKey, opts?: { autoScroll?: boolean }) => {
    withProfileSwitch(() => navigate(`/songs/${songId}?instrument=${encodeURIComponent(instrument)}`, { state: { backTo: location.pathname, ...opts } }));
    /* v8 ignore stop */
  }, [withProfileSwitch, navigate, location.pathname]);

  /* v8 ignore start — navigation helper */
  const navigateToLeaderboard = useCallback((instrument: InstrumentKey | null, metric: RankingMetric, rank?: number) => {
    withProfileSwitch(() => {
      const page = getLeaderboardPageForRank(rank ?? 0);
      if (instrument) {
        navigate(Routes.fullRankings(instrument, metric, page));
      } else {
        const params = new URLSearchParams({ rankBy: metric, page: String(page) });
        navigate(`${Routes.leaderboards}?${params.toString()}`);
      }
    });
    /* v8 ignore stop */
  }, [withProfileSwitch, navigate]);

  const isWideDesktop = useIsWideDesktop();

  // Build a completely flat list of small items — each becomes a direct child
  // of the grid so each gets a staggered fade-in animation.
  const items: PlayerItem[] = [];

  const cardStyle: CSSProperties = {
    ...frostedCard,
    borderRadius: Radius.md,
  };

  // --- Sync banner (with graceful exit collapse) ---
  if (bannerVisible || !bannerCollapsed) {
    const bannerContent = isSyncing ? (
      <SyncBanner
        phase={syncPhase}
        backfillProgress={backfillProgress}
        historyProgress={historyProgress}
        rivalsProgress={rivalsProgress}
        itemsCompleted={itemsCompleted}
        totalItems={totalItems}
        entriesFound={entriesFound}
        currentSongName={currentSongName}
        seasonsQueried={seasonsQueried}
        rivalsFound={rivalsFound}
        isThrottled={isThrottled}
        throttleStatusKey={throttleStatusKey}
        probeStatusKey={probeStatusKey}
        nextRetrySeconds={nextRetrySeconds}
      />
    ) : (showCompleteBanner && onCompleteBannerDismissed) ? (
      <SyncCompleteBanner
        onDismissed={onCompleteBannerDismissed}
        pendingRankUpdate={pendingRankUpdate}
        estimatedRankUpdateMinutes={estimatedRankUpdateMinutes}
      />
    ) : null;

    items.push({
      key: 'sync-slot',
      span: true,
      heightEstimate: isSyncing ? 150 : 80,
      node: (
        <CollapseOnExit show={bannerVisible} onCollapsed={() => setBannerCollapsed(true)}>
          {bannerContent}
        </CollapseOnExit>
      ),
    });
  }

  // --- Overall summary stat boxes ---
  const overallSummaryItems = buildOverallSummaryItems(t, overallStats, songs.length, visibleKeys, navigateToSongs, navigateToSongDetail, cardStyle, statsData?.compositeRanks, settings.enableExperimentalRanks, navigateToLeaderboard);
  items.push(...overallSummaryItems);

  // --- Instrument Statistics heading ---
  items.push({
    key: 'inst-heading',
    span: true,
    heightEstimate: 80,
    node: (
      <PlayerSectionHeading title={t('player.instrumentStats')} description={t('player.instrumentStatsDesc', { name: data.displayName })} />
    ),
  });

  // --- Per-instrument: header + stat boxes + percentile rows ---
  // Render a section for every visible instrument, even if the player has no
  // scores on it yet — the section will show a "No scores yet" empty state.
  const instrumentSectionFirstKeys = new Map<InstrumentKey, string>();
  for (const inst of visibleKeys) {
    const scores = byInstrument.get(inst) ?? [];

    // Prefer pre-computed tiered stats from backend; fall back to client-side computation
    const instTiers = statsData ? getInstrumentTiers(statsData.instruments, inst) : undefined;
    const tier = instTiers ? findStatsTier(instTiers, effectiveLeeway) : undefined;
    const stats = tier ? tierToInstrumentStats(tier) : computeInstrumentStats(scores, songs.length);
    const overThreshold = tier
      ? (filterInvalidScores ? tier.overThresholdCount : undefined)
      : (filterInvalidScores && isScoreValid
        ? (rawByInstrument.get(inst) ?? scores).filter(s => s.score > 0 && !isScoreValid(s.songId, inst, s.score)).length
        : undefined);

    const rankingDto = rankingQueryResults[visibleKeys.indexOf(inst)];
    const totalRanked = hasRankTiers
      ? (statsData!.instrumentRanks as InstrumentRankEntry[])?.find(e => e.ins === comboIdFromInstruments([inst]))?.totalRanked
      : rankingDto?.totalRankedAccounts;
    const instrumentItems = buildInstrumentStatsItems(t, inst, stats, data.displayName, navigateToSongs, navigateToSongDetail, cardStyle, overThreshold, instrumentRankings.get(inst), settings.enableExperimentalRanks, navigateToLeaderboard, data.accountId, totalRanked, leaderboardsEnabled);
    if (instrumentItems.length > 0) {
      instrumentSectionFirstKeys.set(inst, instrumentItems[0]!.key);
    }
    items.push(...instrumentItems);
  }

  // --- Top Songs heading ---
  const hasFab = useIsMobile();
  const isNarrowGrid = useMediaQuery(QUERY_NARROW_GRID);

  items.push({
    key: 'top-heading',
    span: true,
    heightEstimate: 80,
    node: (
      <PlayerSectionHeading title={t('player.topSongsPerInstrument')} description={t('player.topSongsPerInstrumentDesc', {name: data.displayName})} />
    ),
  });

  // Render a Top Songs block for every visible instrument; those without ranked
  // scores will show the same empty state used in the Instrument Stats block.
  for (let i = 0; i < visibleKeys.length; i++) {
    const inst = visibleKeys[i]!;
    const scores = byInstrument.get(inst) ?? [];
    items.push(...buildTopSongsItems(t, inst, scores, songMap, data.displayName, navigateToSongDetail, i === visibleKeys.length - 1, isNarrowGrid));
  }

  const hasBandsSection = playerBandsEnabled && !!statsData;
  if (playerBandsEnabled && statsData) {
    items.push(...buildPlayerBandsItems(t, data.displayName, statsData.bands ?? EMPTY_PLAYER_BANDS));
  }

  const visibleStaggerItemCount = useMemo(() => estimateVisiblePlayerGridItemCount(items), [items]);
  const desktopRailRevealDelayMs = skipAnim
    ? 0
    : staggerCompletionDelay(visibleStaggerItemCount, STAGGER_ENTRY_OFFSET, FADE_DURATION);

  const quickLinks = useMemo<PlayerQuickLink[]>(() => {
    const links: PlayerQuickLink[] = [];
    const firstOverallKey = overallSummaryItems[0]?.key;
    if (firstOverallKey) {
      links.push({
        id: 'global',
        label: t('player.globalStatistics'),
        landmarkLabel: t('player.globalStatistics'),
        itemKey: firstOverallKey,
        icon: <IoStatsChart size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      });
    }

    for (const inst of visibleKeys) {
      const itemKey = instrumentSectionFirstKeys.get(inst);
      if (!itemKey) continue;
      const label = t('player.instrumentStatisticsLink', { instrument: serverInstrumentLabel(inst) });
      links.push({
        id: `instrument:${inst}`,
        label,
        landmarkLabel: label,
        itemKey,
        icon: (
          <InstrumentIcon
            instrument={inst}
            size={QUICK_LINK_GLYPH_ICON_SIZE}
            style={{
              transform: `scale(${QUICK_LINK_INSTRUMENT_ICON_SCALE})`,
              transformOrigin: 'center',
            }}
          />
        ),
      });
    }

    links.push({
      id: 'top-songs',
      label: t('player.topSongsShort'),
      landmarkLabel: t('player.topSongsPerInstrument'),
      itemKey: 'top-heading',
        icon: <IoMusicalNotes size={QUICK_LINK_GLYPH_ICON_SIZE} />,
    });

    if (hasBandsSection) {
      links.push({
        id: 'bands',
        label: t('player.bandsShort'),
        landmarkLabel: t('player.bands', { name: data.displayName }),
        itemKey: 'bands-heading',
        icon: <IoPeople size={QUICK_LINK_GLYPH_ICON_SIZE} />,
      });
    }

    return links;
  }, [data.displayName, hasBandsSection, instrumentSectionFirstKeys, overallSummaryItems, t, visibleKeys]);

  const quickLinkByItemKey = useMemo(() => new Map(quickLinks.map((link) => [link.itemKey, link])), [quickLinks]);

  const {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  } = usePageQuickLinks<PlayerQuickLink>({
    items: quickLinks,
    scrollContainerRef,
    isDesktopRailEnabled: isWideDesktop,
    scrollOffset: QUICK_LINK_SCROLL_OFFSET,
    scrollCompleteThreshold: QUICK_LINK_SCROLL_COMPLETE_THRESHOLD,
    scrollSettleDelayMs: QUICK_LINK_SCROLL_SETTLE_DELAY_MS,
  });

  const handleModalQuickLinkSelect = useCallback((link: PlayerQuickLink) => {
    closeQuickLinks();
    handleQuickLinkSelect(link);
  }, [closeQuickLinks, handleQuickLinkSelect]);

  // Wire up per-item scroll fade for the player grid.
  const fadeDeps = useMemo(() => [items.length], [items.length]);
  useScrollFade(scrollContainerRef, gridListRef, fadeDeps);

  // Show the select-profile pill on all platforms (header actions slot handles layout)
  const selectBtnVisible = !isTrackedPlayer && trackedPlayer?.accountId !== data.accountId;
  const [selectBtnMounted, setSelectBtnMounted] = useState(() => selectBtnVisible || getPendingSelectProfileExitDelay(data.accountId) > 0);

  useEffect(() => {
    if (selectBtnVisible) {
      clearPendingSelectProfileExitTimer();
      clearPendingSelectProfileExit(data.accountId);
      setSelectBtnMounted(true);
      return;
    }

    if (!selectBtnMounted) {
      return;
    }

    clearPendingSelectProfileExitTimer();
    const exitDelay = getPendingSelectProfileExitDelay(data.accountId) || TRANSITION_MS;
    pendingSelectProfileExitTimerRef.current = window.setTimeout(() => {
      pendingSelectProfileExitTimerRef.current = null;
      clearPendingSelectProfileExit(data.accountId);
      setSelectBtnMounted(false);
    }, exitDelay);
  }, [clearPendingSelectProfileExitTimer, data.accountId, selectBtnMounted, selectBtnVisible]);

  const quickLinksTitle = t('player.quickLinks');
  const pageQuickLinks = useMemo<PageQuickLinksConfig>(() => ({
    title: quickLinksTitle,
    items: quickLinks,
    activeItemId,
    visible: quickLinksOpen,
    onOpen: openQuickLinks,
    onClose: closeQuickLinks,
    desktopRailRevealDelayMs: isWideDesktop ? desktopRailRevealDelayMs : 0,
    onSelect: (item) => {
      const nextItem = item as PlayerQuickLink;
      if (isWideDesktop) {
        handleQuickLinkSelect(nextItem);
        return;
      }
      handleModalQuickLinkSelect(nextItem);
    },
    testIdPrefix: 'player',
  }), [activeItemId, closeQuickLinks, desktopRailRevealDelayMs, handleModalQuickLinkSelect, handleQuickLinkSelect, isWideDesktop, openQuickLinks, quickLinks, quickLinksOpen, quickLinksTitle]);

  const quickLinksAction = !isWideDesktop && quickLinks.length > 0
    ? (
      <ActionPill
        icon={<IoCompass size={QUICK_LINK_PILL_ICON_SIZE} />}
        label={quickLinksTitle}
        onClick={openQuickLinks}
      />
    )
    : null;
  const playerHeaderActions = (quickLinksAction || selectBtnMounted) ? (
    <div
      data-testid="player-header-actions"
      style={{
        ...PLAYER_HEADER_ACTIONS_STYLE,
        gap: quickLinksAction && selectBtnVisible ? Gap.md : Gap.none,
      }}
    >
      {selectBtnMounted ? (
        <div
          data-testid="player-select-profile-slot"
          aria-hidden={!selectBtnVisible}
          style={{
            ...SELECT_PROFILE_ACTION_SLOT_STYLE,
            maxWidth: selectBtnVisible
              ? (hasFab ? Layout.pillButtonHeight : SELECT_PROFILE_ACTION_SLOT_DESKTOP_MAX_WIDTH)
              : 0,
            opacity: selectBtnVisible ? 1 : 0,
          }}
        >
          <SelectProfilePill
            visible={selectBtnVisible}
            isMobile={hasFab}
            onClick={() => {
              /* v8 ignore start */
              if (trackedPlayer && trackedPlayer.accountId !== data.accountId) {
                  setPendingSwitch(() => primeTrackedPlayerSelection);
              } else {
                  primeTrackedPlayerSelection();
              /* v8 ignore stop */
              }
            }}
          />
        </div>
      ) : null}
      {quickLinksAction}
    </div>
  ) : null;

  const mobilePlayerHeaderActions = playerHeaderActions ? (
    <PageHeaderActionsTransition
      visible={settings.showButtonsInHeaderMobile}
      testId="player-header-actions-transition"
    >
      {playerHeaderActions}
    </PageHeaderActionsTransition>
  ) : undefined;

  return (
    <Page
      scrollRestoreKey={`statistics:${data.accountId}`}
      scrollDeps={fadeDeps}
      scrollStyle={pps.scrollArea}
      quickLinks={quickLinks.length > 0 ? pageQuickLinks : undefined}
      before={hasFab ? (
        <PageHeader
          title={data.displayName}
          actions={mobilePlayerHeaderActions}
        />
      ) : (
        <PageHeader
          title={data.displayName}
          actions={playerHeaderActions ?? undefined}
        />
      )}
      after={<>
        {pendingSwitch ? (
          <ConfirmAlert
            title={t('player.switchTo', {name: data.displayName})}
            message={t('player.switchConfirmMessage', {name: data.displayName})}
            /* v8 ignore start */
            onNo={() => setPendingSwitch(null)}
            onYes={() => pendingSwitch()}
            onExitComplete={() => setPendingSwitch(null)}
            /* v8 ignore stop */
          />
        ) : null}
      </>}
    >
        <div style={{ ...(hasFab ? { paddingBottom: Layout.fabPaddingBottom } : {}) }}>
            <div ref={gridListRef} style={{ ...pps.gridList, ...(isNarrowGrid ? { gridTemplateColumns: 'minmax(0, 1fr)' } : {}) }}>
            {(() => {
              const visibleCount = visibleStaggerItemCount;
              const lastVisibleDelay = visibleCount * STAGGER_ENTRY_OFFSET;
              return items.map((item, i) => {
                const delay = skipAnim ? undefined : (i < visibleCount ? (i + 1) * STAGGER_ENTRY_OFFSET : lastVisibleDelay);
                const quickLink = quickLinkByItemKey.get(item.key);
                const itemTestId = item.key.endsWith('-pct-table') ? `player-item-${item.key}` : undefined;
                const content = quickLink ? (
                  <section
                    ref={(element) => registerSectionRef(quickLink.id, element)}
                    data-player-section={quickLink.id}
                    data-testid={`player-section-${getPageQuickLinkTestId(quickLink.id)}`}
                    aria-label={quickLink.landmarkLabel}
                    style={pps.sectionLandmark}
                  >
                    {item.node}
                  </section>
                ) : item.node;
                return (
                  <FadeIn key={item.key} data-testid={itemTestId} delay={delay} style={item.span ? { ...pps.gridFullWidth, ...item.style } : item.style}>
                    {content}
                  </FadeIn>
                );
              });
            })()}
            </div>
        </div>
    </Page>
  );
}
