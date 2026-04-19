/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useRef, useState, useCallback, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useLocation } from 'react-router-dom';
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
import { Gap, Layout, Radius, frostedCard, STAGGER_ENTRY_OFFSET, QUERY_NARROW_GRID } from '@festival/theme';
import { playerPageStyles as pps } from '../../../../components/player/playerPageStyles';
import { SelectProfilePill } from '../../../../components/player/SelectProfilePill';
import SyncBanner from '../../../../components/page/SyncBanner';
import SyncCompleteBanner from '../../../../components/page/SyncCompleteBanner';
import CollapseOnExit from '../../../../components/page/CollapseOnExit';
import { useSettings, isInstrumentVisible } from '../../../../contexts/SettingsContext';
import { loadSongSettings, saveSongSettings } from '../../../../utils/songSettings';
import Page from '../../../Page';
import PageHeader from '../../../../components/common/PageHeader';
import { useIsMobile, useIsWideDesktop } from '../../../../hooks/ui/useIsMobile';
import { useMediaQuery } from '../../../../hooks/ui/useMediaQuery';
import { useTrackedPlayer } from '../../../../hooks/data/useTrackedPlayer';
import { useScoreFilter } from '../../../../hooks/data/useScoreFilter';
import { usePlayerPageSelect } from '../../../../contexts/FabSearchContext';
import { useSearchQuery } from '../../../../contexts/SearchQueryContext';
import { useScrollContainer } from '../../../../contexts/ScrollContainerContext';
import ConfirmAlert from '../../../../components/modals/ConfirmAlert';
import FadeIn from '../../../../components/page/FadeIn';
import PlayerSectionHeading from '../../../player/sections/PlayerSectionHeading';
import { buildOverallSummaryItems } from '../../../player/sections/OverallSummarySection';
import { buildInstrumentStatsItems } from '../../../player/sections/InstrumentStatsSection';
import { buildTopSongsItems } from '../../../player/components/TopSongsSection';
import { buildPlayerBandsItems, EMPTY_PLAYER_BANDS } from '../../../player/components/PlayerBandsSection';
import type { PlayerItem } from '../../../player/helpers/playerPageTypes';
import type { SyncPhase } from '../../../../hooks/data/useSyncStatus';
import { Routes } from '../../../../routes';
import type { AccountRankingEntry, RankingMetric, InstrumentRankEntry, AccountRankingDto, PlayerStatsResponse } from '@festival/core/api/serverTypes';
import { useFeatureFlags } from '../../../../contexts/FeatureFlagsContext';

type PlayerQuickLinkId = 'global' | 'top-songs' | 'bands' | `instrument:${InstrumentKey}`;

interface PlayerQuickLink {
  id: PlayerQuickLinkId;
  label: string;
  landmarkLabel: string;
  itemKey: string;
}

const QUICK_LINK_SCROLL_OFFSET = Gap.md;

function getSectionScrollTop(scrollEl: HTMLElement, sectionEl: HTMLElement): number {
  const scrollRect = scrollEl.getBoundingClientRect();
  const sectionRect = sectionEl.getBoundingClientRect();
  return scrollEl.scrollTop + sectionRect.top - scrollRect.top;
}

function resolveActiveQuickLink(
  quickLinks: readonly PlayerQuickLink[],
  sectionRefs: Map<PlayerQuickLinkId, HTMLElement>,
  scrollEl: HTMLElement,
): PlayerQuickLinkId | null {
  if (quickLinks.length === 0) return null;

  const threshold = scrollEl.scrollTop + QUICK_LINK_SCROLL_OFFSET + 1;
  let active = quickLinks[0]!.id;

  for (const link of quickLinks) {
    const sectionEl = sectionRefs.get(link.id);
    if (!sectionEl) continue;
    if (getSectionScrollTop(scrollEl, sectionEl) <= threshold) {
      active = link.id;
      continue;
    }
    break;
  }

  return active;
}

function getQuickLinkTestId(id: PlayerQuickLinkId): string {
  return id.replace(/[^a-z0-9]+/gi, '-').toLowerCase();
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
  const [pendingSwitch, setPendingSwitch] = useState<(() => void) | null>(null);
  const [activeQuickLink, setActiveQuickLink] = useState<PlayerQuickLinkId | null>(null);
  const bannerVisible = isSyncing || !!(showCompleteBanner && onCompleteBannerDismissed);
  const [bannerCollapsed, setBannerCollapsed] = useState(!bannerVisible);
  useEffect(() => { if (bannerVisible) setBannerCollapsed(false); }, [bannerVisible]);
  const { filterPlayerScores, isScoreValid, enabled: filterInvalidScores, leeway } = useScoreFilter();
  const { registerPlayerPageSelect } = usePlayerPageSelect();
  const sectionRefs = useRef(new Map<PlayerQuickLinkId, HTMLElement>());

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
          setPendingSwitch(() => () => setPlayer({ accountId: data.accountId, displayName: data.displayName }));
        } else {
          setPlayer({ accountId: data.accountId, displayName: data.displayName });
        /* v8 ignore stop */
        }
      },
    });
    return () => registerPlayerPageSelect(null);
  }, [data.accountId, data.displayName, trackedPlayer, setPlayer, registerPlayerPageSelect]);

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
  const navigateToLeaderboard = useCallback((instrument: InstrumentKey | null, metric: RankingMetric) => {
    withProfileSwitch(() => {
      if (instrument) {
        navigate(Routes.fullRankings(instrument, metric));
      } else {
        navigate(`${Routes.leaderboards}?rankBy=${encodeURIComponent(metric)}`);
      }
    });
    /* v8 ignore stop */
  }, [withProfileSwitch, navigate]);

  const isWideDesktop = useIsWideDesktop();

  const registerSectionRef = useCallback((id: PlayerQuickLinkId, element: HTMLElement | null) => {
    if (element) {
      sectionRefs.current.set(id, element);
      return;
    }
    sectionRefs.current.delete(id);
  }, []);

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

  const quickLinks = useMemo<PlayerQuickLink[]>(() => {
    const links: PlayerQuickLink[] = [];
    const firstOverallKey = overallSummaryItems[0]?.key;
    if (firstOverallKey) {
      links.push({
        id: 'global',
        label: t('player.globalStatistics'),
        landmarkLabel: t('player.globalStatistics'),
        itemKey: firstOverallKey,
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
      });
    }

    links.push({
      id: 'top-songs',
      label: t('player.topSongsPerInstrument'),
      landmarkLabel: t('player.topSongsPerInstrument'),
      itemKey: 'top-heading',
    });

    if (hasBandsSection) {
      links.push({
        id: 'bands',
        label: t('player.bandsShort'),
        landmarkLabel: t('player.bands', { name: data.displayName }),
        itemKey: 'bands-heading',
      });
    }

    return links;
  }, [data.displayName, hasBandsSection, instrumentSectionFirstKeys, overallSummaryItems, t, visibleKeys]);

  const quickLinkByItemKey = useMemo(() => new Map(quickLinks.map((link) => [link.itemKey, link])), [quickLinks]);

  useEffect(() => {
    const scrollEl = scrollContainerRef.current;
    if (!isWideDesktop || !scrollEl || quickLinks.length === 0) {
      setActiveQuickLink(null);
      return;
    }

    const syncActive = () => {
      setActiveQuickLink(resolveActiveQuickLink(quickLinks, sectionRefs.current, scrollEl));
    };

    syncActive();
    scrollEl.addEventListener('scroll', syncActive, { passive: true });
    window.addEventListener('resize', syncActive);
    return () => {
      scrollEl.removeEventListener('scroll', syncActive);
      window.removeEventListener('resize', syncActive);
    };
  }, [isWideDesktop, quickLinks, scrollContainerRef]);

  const handleQuickLinkClick = useCallback((link: PlayerQuickLink) => {
    const scrollEl = scrollContainerRef.current;
    const sectionEl = sectionRefs.current.get(link.id);
    if (!scrollEl || !sectionEl) return;

    const nextTop = Math.max(0, getSectionScrollTop(scrollEl, sectionEl) - QUICK_LINK_SCROLL_OFFSET);
    scrollEl.scrollTo({ top: nextTop, behavior: 'smooth' });
    setActiveQuickLink(link.id);
  }, [scrollContainerRef]);

  // Wire up container-level scroll fade
  const fadeDeps = useMemo(() => [items.length], [items.length]);

  // Show the select-profile pill on all platforms (header actions slot handles layout)
  const canShowSelectBtn = true;
  const selectBtnVisible = !isTrackedPlayer && trackedPlayer?.accountId !== data.accountId;

  return (
    <Page
      scrollRestoreKey={`statistics:${data.accountId}`}
      scrollDeps={fadeDeps}
      scrollMaskOptions={{ disabled: true }}
      scrollStyle={pps.scrollArea}
      before={
        <PageHeader
          title={data.displayName}
          actions={canShowSelectBtn ? (
            <SelectProfilePill
              visible={selectBtnVisible}
              isMobile={hasFab}
              onClick={() => {
                /* v8 ignore start */
                if (trackedPlayer && trackedPlayer.accountId !== data.accountId) {
                  setPendingSwitch(() => () => setPlayer({ accountId: data.accountId, displayName: data.displayName }));
                } else {
                  setPlayer({ accountId: data.accountId, displayName: data.displayName });
                /* v8 ignore stop */
                }
              }}
            />
          ) : undefined}
        />
      }
      after={pendingSwitch ? (
        <ConfirmAlert
          title={t('player.switchTo', {name: data.displayName})}
          message={t('player.switchConfirmMessage', {name: data.displayName})}
          /* v8 ignore start */
          onNo={() => setPendingSwitch(null)}
          onYes={() => pendingSwitch()}
          onExitComplete={() => setPendingSwitch(null)}
          /* v8 ignore stop */
        />
      ) : undefined}
    >
        <div style={{ ...(hasFab ? { paddingBottom: Layout.fabPaddingBottom } : {}) }}>
          <div style={pps.overlayFrame}>
            {isWideDesktop && quickLinks.length > 0 && (
              <div style={pps.quickLinksOverlay}>
                <nav style={pps.quickLinksSticky} aria-label={t('player.quickLinks')}>
                  {quickLinks.map((link) => {
                    const isActive = link.id === activeQuickLink;
                    return (
                      <button
                        key={link.id}
                        type="button"
                        data-testid={`player-quick-link-${getQuickLinkTestId(link.id)}`}
                        aria-current={isActive ? 'location' : undefined}
                        style={isActive ? pps.quickLinkButtonActive : pps.quickLinkButton}
                        onClick={() => handleQuickLinkClick(link)}
                      >
                        {link.label}
                      </button>
                    );
                  })}
                </nav>
              </div>
            )}
            <div style={{ ...pps.gridList, ...(isNarrowGrid ? { gridTemplateColumns: 'minmax(0, 1fr)' } : {}) }}>
            {(() => {
              // Compute which items are in the initial viewport by accumulating
              // estimated row heights.  The grid is 2-col: span items take a full
              // row, non-span items pair up (each row = max of the pair's height).
              /* v8 ignore start -- SSR guard: window always defined in jsdom */
              const vh = typeof window !== 'undefined' ? window.innerHeight : 900;
              /* v8 ignore stop */
              const gap = Gap.md;
              let accHeight = 0;
              let col = 0; // 0 = left, 1 = right in the 2-col grid
              let rowMax = 0;
              let visibleCount = items.length; // default: animate all

              for (let i = 0; i < items.length; i++) {
                const item = items[i]!;
                if (item.span) {
                  // Flush any pending half-row
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
                  visibleCount = i + 2; // +1 for the partially-visible item, +1 for 0-index
                }
              }

              const lastVisibleDelay = visibleCount * STAGGER_ENTRY_OFFSET;
              return items.map((item, i) => {
                const delay = skipAnim ? undefined : (i < visibleCount ? (i + 1) * STAGGER_ENTRY_OFFSET : lastVisibleDelay);
                const quickLink = quickLinkByItemKey.get(item.key);
                const content = quickLink ? (
                  <section
                    ref={(element) => registerSectionRef(quickLink.id, element)}
                    data-player-section={quickLink.id}
                    data-testid={`player-section-${getQuickLinkTestId(quickLink.id)}`}
                    aria-label={quickLink.landmarkLabel}
                    style={pps.sectionLandmark}
                  >
                    {item.node}
                  </section>
                ) : item.node;
                return (
                  <FadeIn key={item.key} delay={delay} style={item.span ? { ...pps.gridFullWidth, ...item.style } : item.style}>
                    {content}
                  </FadeIn>
                );
              });
            })()}
            </div>
          </div>
        </div>
    </Page>
  );
}
