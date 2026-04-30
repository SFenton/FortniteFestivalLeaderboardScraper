import { HashRouter, Routes, Route, Navigate, useLocation, useNavigate, useNavigationType } from 'react-router-dom';
import { IoCompass, IoPerson, IoPersonAdd, IoSearch, IoSwapVerticalSharp, IoFunnel, IoFlash, IoBagHandle, IoGrid, IoList, IoOptions, IoMusicalNotes, IoTrophy } from 'react-icons/io5';
import { useEffect, useState, useMemo, useRef, useCallback, Suspense, lazy } from 'react';
import { useTranslation } from 'react-i18next';
import type { TFunction } from 'i18next';
import { FestivalProvider, useFestival } from './contexts/FestivalContext';
import { SettingsProvider } from './contexts/SettingsContext';
import { ShopProvider } from './contexts/ShopContext';
import { AnimatedBackground } from './components/shell/AnimatedBackground';
import { useTrackedPlayer, type TrackedPlayer } from './hooks/data/useTrackedPlayer';
import { usePlayerBandsPrefetch } from './hooks/data/usePlayerBandsPrefetch';
import type { SelectedProfile } from './hooks/data/useSelectedProfile';
import { PlayerDataProvider } from './contexts/PlayerDataContext';
import { useIsMobile, useIsMobileChrome, useIsWideDesktop } from './hooks/ui/useIsMobile';
import { useMediaQuery } from './hooks/ui/useMediaQuery';
import SongsPage from './pages/songs/SongsPage';
/* v8 ignore start -- lazy() wrappers are resolved by the bundler, not callable in unit tests */
const SongDetailPage = lazy(() => import('./pages/songinfo/SongDetailPage'));
const LeaderboardPage = lazy(() => import('./pages/leaderboard/global/LeaderboardPage'));
const SongBandLeaderboardPage = lazy(() => import('./pages/leaderboard/band/SongBandLeaderboardPage'));
const PlayerHistoryPage = lazy(() => import('./pages/leaderboard/player/PlayerHistoryPage'));
const PlayerPage = lazy(() => import('./pages/player/PlayerPage'));
const SuggestionsPage = lazy(() => import('./pages/suggestions/SuggestionsPage'));
const SettingsPage = lazy(() => import('./pages/settings/SettingsPage'));
const ShopPage = lazy(() => import('./pages/shop/ShopPage'));
const RivalsPage = lazy(() => import('./pages/rivals/RivalsPage'));
const RivalDetailPage = lazy(() => import('./pages/rivals/RivalDetailPage'));
const RivalCategoryPage = lazy(() => import('./pages/rivals/RivalryPage'));
const AllRivalsPage = lazy(() => import('./pages/rivals/AllRivalsPage'));
const LeaderboardsOverviewPage = lazy(() => import('./pages/leaderboards/LeaderboardsOverviewPage'));
const FullRankingsPage = lazy(() => import('./pages/leaderboards/FullRankingsPage'));
const BandRankingsPage = lazy(() => import('./pages/leaderboards/BandRankingsPage'));
const BandPage = lazy(() => import('./pages/band/BandPage'));
const PlayerBandsPage = lazy(() => import('./pages/band/PlayerBandsPage'));
const CompetePage = lazy(() => import('./pages/compete/CompetePage'));
/* v8 ignore stop */
import { Size, Layout, QUERY_NARROW_GRID } from '@festival/theme';

/** Shared route tree used by both mobile and wide-desktop layouts. */
function RoutesContent({ player }: { player: TrackedPlayer | null }) {
  return (
    <Suspense fallback={<SuspenseFallback />}>
    <Routes>
      <Route path="/" element={<Navigate to={AppRoutes.songs} replace />} />
      <Route path="/songs" element={<SongsPage />} />
      <Route path="/songs/:songId" element={<ErrorBoundary fallback={<RouteErrorFallback />}><SongDetailPage /></ErrorBoundary>} />
      <Route path="/songs/:songId/bands/:bandType" element={<FeatureGate flag="playerBands"><ErrorBoundary fallback={<RouteErrorFallback />}><SongBandLeaderboardPage /></ErrorBoundary></FeatureGate>} />
      <Route path="/songs/:songId/:instrument" element={<ErrorBoundary fallback={<RouteErrorFallback />}><LeaderboardPage /></ErrorBoundary>} />
      <Route path="/songs/:songId/:instrument/history" element={<ErrorBoundary fallback={<RouteErrorFallback />}><PlayerHistoryPage /></ErrorBoundary>} />
      <Route path="/player/:accountId" element={<ErrorBoundary fallback={<RouteErrorFallback />}><PlayerPage /></ErrorBoundary>} />
      {player ? (
        <Route path="/rivals" element={<ErrorBoundary fallback={<RouteErrorFallback />}><RivalsPage /></ErrorBoundary>} />
      ) : (
        <Route path="/rivals" element={<Navigate to={AppRoutes.songs} replace />} />
      )}
      {player ? (
        <Route path="/rivals/all" element={<ErrorBoundary fallback={<RouteErrorFallback />}><AllRivalsPage /></ErrorBoundary>} />
      ) : (
        <Route path="/rivals/all" element={<Navigate to={AppRoutes.songs} replace />} />
      )}
      {player ? (
        <Route path="/rivals/:rivalId" element={<ErrorBoundary fallback={<RouteErrorFallback />}><RivalDetailPage /></ErrorBoundary>} />
      ) : (
        <Route path="/rivals/:rivalId" element={<Navigate to={AppRoutes.songs} replace />} />
      )}
      {player ? (
        <Route path="/rivals/:rivalId/rivalry" element={<ErrorBoundary fallback={<RouteErrorFallback />}><RivalCategoryPage /></ErrorBoundary>} />
      ) : (
        <Route path="/rivals/:rivalId/rivalry" element={<Navigate to={AppRoutes.songs} replace />} />
      )}
      {player ? (
        <Route path="/statistics" element={<ErrorBoundary fallback={<RouteErrorFallback />}><PlayerPage accountId={player.accountId} /></ErrorBoundary>} />
      ) : (
        <Route path="/statistics" element={<Navigate to={AppRoutes.songs} replace />} />
      )}
      {player ? (
        <Route path="/suggestions" element={<ErrorBoundary fallback={<RouteErrorFallback />}><SuggestionsPage accountId={player.accountId} /></ErrorBoundary>} />
      ) : (
        <Route path="/suggestions" element={<Navigate to={AppRoutes.songs} replace />} />
      )}
      <Route path="/shop" element={<ErrorBoundary fallback={<RouteErrorFallback />}><ShopPage /></ErrorBoundary>} />
      <Route path="/leaderboards" element={<ErrorBoundary fallback={<RouteErrorFallback />}><LeaderboardsOverviewPage /></ErrorBoundary>} />
      <Route path="/leaderboards/all" element={<ErrorBoundary fallback={<RouteErrorFallback />}><FullRankingsPage /></ErrorBoundary>} />
      <Route path="/leaderboards/bands/:bandType" element={<FeatureGate flag="playerBands"><ErrorBoundary fallback={<RouteErrorFallback />}><BandRankingsPage /></ErrorBoundary></FeatureGate>} />
      <Route path="/bands/player/:accountId" element={<FeatureGate flag="playerBands"><ErrorBoundary fallback={<RouteErrorFallback />}><PlayerBandsPage /></ErrorBoundary></FeatureGate>} />
      <Route path="/bands" element={<ErrorBoundary fallback={<RouteErrorFallback />}><BandPage /></ErrorBoundary>} />
      <Route path="/bands/:bandId" element={<ErrorBoundary fallback={<RouteErrorFallback />}><BandPage /></ErrorBoundary>} />
      {player ? (
        <Route path="/compete" element={<ErrorBoundary fallback={<RouteErrorFallback />}><CompetePage /></ErrorBoundary>} />
      ) : (
        <Route path="/compete" element={<Navigate to={AppRoutes.songs} replace />} />
      )}
      <Route path="/settings" element={<ErrorBoundary fallback={<RouteErrorFallback />}><SettingsPage /></ErrorBoundary>} />
    </Routes>
    </Suspense>
  );
}
import { appStyles } from './appStyles';
import { resetSongSettingsForDeselect, loadSongSettings, SONG_SETTINGS_CHANGED_EVENT } from './utils/songSettings';
import BackLink from './components/shell/mobile/BackLink';
import MobileHeader from './components/shell/mobile/MobileHeader';
import { FabSearchProvider, useFabSearch } from './contexts/FabSearchContext';
import { PageQuickLinksProvider, usePageQuickLinksController } from './contexts/PageQuickLinksContext';
import { BandFilterActionProvider, type BandFilterActionContextValue } from './contexts/BandFilterActionContext';
import { SearchQueryProvider } from './contexts/SearchQueryContext';
import { useSettings, visiblePathInstruments } from './contexts/SettingsContext';
import { useFeatureFlags } from './contexts/FeatureFlagsContext';
import { useProximityGlow } from './hooks/ui/useProximityGlow';
import BottomNav from './components/shell/mobile/BottomNav';
import Sidebar from './components/shell/desktop/Sidebar';
import DesktopNav from './components/shell/desktop/DesktopNav';
import PinnedSidebar from './components/shell/desktop/PinnedSidebar';
import FloatingActionButton, { type ActionItem } from './components/shell/fab/FloatingActionButton';
import MobilePlayerSearchModal from './components/shell/mobile/MobilePlayerSearchModal';
import SearchModal from './components/search/SearchModal';
import { clearSongDetailCache, clearLeaderboardCache, clearPlayerPageCache } from './api/pageCache';
import { IS_IOS, IS_ANDROID, IS_PWA, IS_PAGE_RELOAD } from '@festival/ui-utils';
import ChangelogModal from './components/modals/ChangelogModal';
import ConfirmAlert from './components/modals/ConfirmAlert';
import BandInstrumentFilterModal, { type BandInstrumentFilterAssignment } from './pages/band/modals/BandInstrumentFilterModal';
import { APP_VERSION } from './hooks/data/useVersions';
import { changelogHash } from './changelog';
import ErrorBoundary from './components/page/ErrorBoundary';
import SuspenseFallback from './components/common/SuspenseFallback';
import RouteErrorFallback from './components/page/RouteErrorFallback';
import { createPreserveShellScrollState, type PreserveShellScrollState } from './utils/quietNavigation';
import { getBandFilterActionLabel } from './utils/bandFilterDisplay';

const consumedPreserveShellScrollKeys = new Set<string>();
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { queryClient } from './api/queryClient';
import { Routes as AppRoutes, RoutePatterns } from './routes';
import { FirstRunProvider, useFirstRunContext } from './contexts/FirstRunContext';
import { useShopState } from './hooks/data/useShopState';
import { ScrollContainerProvider, useShellRefs, useScrollContainer, HEADER_PORTAL_HEIGHT_VAR } from './contexts/ScrollContainerContext';
import { FeatureFlagsProvider } from './contexts/FeatureFlagsContext';
import FeatureGate from './components/routing/FeatureGate';

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
    <FeatureFlagsProvider>
    <SettingsProvider>
      <FestivalProvider>
        <ShopProvider>
        <FirstRunProvider>
        <FabSearchProvider>
        <PageQuickLinksProvider>
          <SearchQueryProvider>
            <HashRouter>
              <ScrollContainerProvider>
              <AppShell />
              </ScrollContainerProvider>
            </HashRouter>
          </SearchQueryProvider>
        </PageQuickLinksProvider>
        </FabSearchProvider>
        </FirstRunProvider>
        </ShopProvider>
      </FestivalProvider>
    </SettingsProvider>
    </FeatureFlagsProvider>
    <ReactQueryDevtools initialIsOpen={false} />
    </QueryClientProvider>
  );
}

import { useTabNavigation } from './hooks/ui/useTabNavigation';

const CHANGELOG_STORAGE_KEY = 'fst:changelog';
const EMPTY_BAND_FILTER_ASSIGNMENTS: BandInstrumentFilterAssignment[] = [];

export function getFabQuickLinksActionLabel(t: TFunction): string {
  return t('common.quickLinks', 'Quick Links');
}

export function shouldShowBandFilterAction(selectedProfile: SelectedProfile | null, pathname: string): boolean {
  return selectedProfile?.type === 'band' && pathname !== AppRoutes.settings;
}

export function prependFabActionGroup(leadingActions: ActionItem[], actionGroups: ActionItem[][]): ActionItem[][] {
  return leadingActions.length > 0 ? [leadingActions, ...actionGroups] : actionGroups;
}

/**
 * Determines the destination action for the compact-desktop profile icon click.
 * Returns the navigation target string, 'sidebar', or 'modal'.
 */
export function getProfileClickDestination(
  player: TrackedPlayer | null,
  selectedProfile: SelectedProfile | null,
): string | 'sidebar' | 'modal' {
  if (player) return AppRoutes.statistics;
  if (selectedProfile?.type === 'band') {
    const { bandId, bandType, teamKey, displayName } = selectedProfile;
    if (bandId && bandType && teamKey) {
      return AppRoutes.band(bandId, { bandType, teamKey, names: displayName });
    }
    return 'sidebar';
  }
  return 'modal';
}

export function mergePageQuickLinksIntoFabGroups(
  quickLinksActions: ActionItem[],
  pageSpecificActions: ActionItem[],
  ...otherGroups: ActionItem[][]
): ActionItem[][] {
  const actionGroups: ActionItem[][] = [];

  if (quickLinksActions.length > 0) {
    actionGroups.push(pageSpecificActions.length > 0 ? [...quickLinksActions, ...pageSpecificActions] : quickLinksActions);
  } else if (pageSpecificActions.length > 0) {
    actionGroups.push(pageSpecificActions);
  }

  return [
    ...actionGroups,
    ...otherGroups.filter(group => group.length > 0),
  ];
}

const ANIMATED_BG_ROUTES = new Set(['/', AppRoutes.songs, AppRoutes.suggestions, AppRoutes.statistics, AppRoutes.settings, AppRoutes.shop, AppRoutes.compete, AppRoutes.leaderboards]);
/* v8 ignore start — route detection helper */
function isAnimatedBgRoute(pathname: string) {
  return ANIMATED_BG_ROUTES.has(pathname) || RoutePatterns.player.test(pathname) || pathname.startsWith('/rivals') || pathname.startsWith('/leaderboards') || pathname.startsWith('/bands');
}
/* v8 ignore stop */

/* v8 ignore start — wide desktop layout wrapper with overlay architecture */
function WideDesktopLayout({
  shellScrollRef,
  shellPortalRefCallback,
  shellQuickLinksRailPortalRefCallback,
  player,
  selectedProfile,
  onDeselect,
  onSelectPlayer,
}: {
  shellScrollRef: React.RefObject<HTMLDivElement | null>;
  shellPortalRefCallback: (el: HTMLDivElement | null) => void;
  shellQuickLinksRailPortalRefCallback: (el: HTMLDivElement | null) => void;
  player: TrackedPlayer | null;
  selectedProfile: ReturnType<typeof useTrackedPlayer>['profile'];
  onDeselect: () => void;
  onSelectPlayer: () => void;
}) {
  return (
    <div style={appStyles.bodySection}>
      {/* Scroll container starts below the header overlay — content can never reach it.
         top is driven by a CSS custom property updated outside React to avoid re-render cascades. */}
      <div ref={shellScrollRef} style={{ ...appStyles.scrollContainerFull, top: `var(${HEADER_PORTAL_HEIGHT_VAR}, 0px)` }}>
        <div style={appStyles.scrollContentRow}>
          <div style={appStyles.sidebarGutter} />
          <div style={appStyles.centerColumn}>
            <div id="main-content" style={{ ...appStyles.content, ...appStyles.contentPinned }}>
              <RoutesContent player={player} />
            </div>
          </div>
          <div style={appStyles.rightGutter} />
        </div>
      </div>
      {/* Sidebar overlay — pointer-events: none lets wheel through */}
      <div style={appStyles.sidebarOverlay}>
        <PinnedSidebar
          player={player}
          selectedProfile={selectedProfile}
          onDeselect={onDeselect}
          onSelectPlayer={onSelectPlayer}
        />
      </div>
      {/* Header overlay — pointer-events: none lets wheel through */}
      <div style={appStyles.headerOverlay}>
        <div style={{ width: Layout.sidebarWidth, flexShrink: 0 }} />
        <div ref={shellPortalRefCallback} style={appStyles.headerPortalWide} />
        <div style={{ width: Layout.sidebarWidth, flexShrink: 0 }} />
      </div>
      {/* Right quick-links overlay — independent chrome outside content scroll */}
      <div style={appStyles.rightRailOverlay}>
        <div style={appStyles.sidebarGutter} />
        <div style={appStyles.centerColumn} />
        <div ref={shellQuickLinksRailPortalRefCallback} style={appStyles.quickLinksRailPortal} data-testid="shell-quick-links-portal" />
      </div>
    </div>
  );
}
/* v8 ignore stop */

function AppShell() {
  const { t } = useTranslation();
  const { profile: selectedProfile, player, setPlayer, clearPlayer } = useTrackedPlayer();
  const { state: { songs } } = useFestival();
  const { settings } = useSettings();
  const { experimentalRanks: experimentalRanksEnabled = false } = useFeatureFlags();

  // Proximity glow for frosted cards — document-level for full coverage
  useProximityGlow(!settings.disableLightTrails);
  usePlayerBandsPrefetch(player?.accountId);

  const location = useLocation();
  const isMobile = useIsMobileChrome();
  const isNarrow = useIsMobile();
  const isNarrowGrid = useMediaQuery(QUERY_NARROW_GRID);
  const isWideDesktop = useIsWideDesktop();
  const hasVisiblePathInstruments = visiblePathInstruments(settings).length > 0;
  const fabSearch = useFabSearch();
  const pageQuickLinks = usePageQuickLinksController();
  const { isShopVisible, getShopUrl } = useShopState();
  const {
    scrollRef: shellScrollRef,
    portalRefCallback: shellPortalRefCallback,
    quickLinksRailPortalRefCallback: shellQuickLinksRailPortalRefCallback,
  } = useShellRefs();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [playerModalOpen, setPlayerModalOpen] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [bandFilterModalOpen, setBandFilterModalOpen] = useState(false);
  const [appliedBandFilter, setAppliedBandFilter] = useState<{
    bandId: string;
    assignments: BandInstrumentFilterAssignment[];
  } | null>(null);
  const [hasNewChangelog] = useState(() => {
    try {
      const stored = localStorage.getItem(CHANGELOG_STORAGE_KEY);
      if (!stored) return true;
      const parsed = JSON.parse(stored);
      return parsed.hash !== changelogHash();
    } catch { return true; }
  });
  const [changelogDismissed, setChangelogDismissed] = useState(false);
  const { activeCarouselKey } = useFirstRunContext();
  /* v8 ignore next — activeCarouselKey suppression tested via FirstRunContext tests */
  const showChangelog = hasNewChangelog && !changelogDismissed && !activeCarouselKey;
  /* v8 ignore start — modal dismiss callback */
  const dismissChangelog = useCallback(() => {
    localStorage.setItem(CHANGELOG_STORAGE_KEY, JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));
  }, []);
  /* v8 ignore stop */
  const navigate = useNavigate();
  const navType = useNavigationType();

  // Track whether the back button has already appeared in the current detail stack.
  const backShownRef = useRef(false);

  // Clear page caches when score filter settings change so pages restagger
  const filterRef = useRef({ e: settings.filterInvalidScores, l: settings.filterInvalidScoresLeeway });
  /* v8 ignore start — deep AppInner: filter change cache invalidation */
  useEffect(() => {
    const prev = filterRef.current;
    if (prev.e !== settings.filterInvalidScores || prev.l !== settings.filterInvalidScoresLeeway) {
      filterRef.current = { e: settings.filterInvalidScores, l: settings.filterInvalidScoresLeeway };
      clearSongDetailCache();
      clearLeaderboardCache();
      clearPlayerPageCache();
      // Invalidate leaderboard queries (server-side filtering required).
      // Player queries are NOT invalidated — the precomputed response includes
      // minLeeway + validScores, so the client handles all leeway values locally.
      queryClient.invalidateQueries({ queryKey: ['leaderboard'] });
      queryClient.invalidateQueries({ queryKey: ['allLeaderboards'] });
      /* v8 ignore stop */
    }
  }, [settings.filterInvalidScores, settings.filterInvalidScoresLeeway]);

  // --- Per-tab stack (mobile only) ---
  const { activeTab, handleTabClick } = useTabNavigation();

  /* v8 ignore start — deep AppInner: routing/navigation logic embedded in render */
  const handleSelect = (p: TrackedPlayer) => {
    setPlayer(p);
    // Tracked profiles live on the statistics tab root; rewrite detail URLs quietly.
    if (location.pathname !== AppRoutes.statistics) {
      const preserveScroll = location.pathname === AppRoutes.player(p.accountId);
      navigate(
        AppRoutes.statistics,
        preserveScroll
          ? { replace: true, state: createPreserveShellScrollState(`profile-select:${p.accountId}`) }
          : { replace: true },
      );
    }
  };
  /* v8 ignore stop */

  /* v8 ignore start — deep AppInner: deselect callback */
  const [showDeselectConfirm, setShowDeselectConfirm] = useState(false);
  const handleDeselect = useCallback(() => {
    if (selectedProfile?.type === 'band') {
      clearPlayer();
      return;
    }
    setShowDeselectConfirm(true);
  }, [clearPlayer, selectedProfile?.type]);
  const confirmDeselect = useCallback(() => {
    resetSongSettingsForDeselect();
    clearPlayer();
    setShowDeselectConfirm(false);
  }, [clearPlayer]);

  /* v8 ignore start — deep AppInner: compact desktop profile icon click */
  const handleProfileClick = useCallback(() => {
    const dest = getProfileClickDestination(player, selectedProfile);
    if (dest === 'sidebar') setSidebarOpen(true);
    else if (dest === 'modal') setPlayerModalOpen(true);
    else navigate(dest);
  }, [navigate, player, selectedProfile]);
  /* v8 ignore stop */

  /* v8 ignore start — deep AppInner: instrument sync event listener */
  const [songInstrument, setSongInstrument] = useState(() => loadSongSettings().instrument);
  useEffect(() => {
    const sync = () => setSongInstrument(loadSongSettings().instrument);
    window.addEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
    return () => window.removeEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
  }, []);
  /* v8 ignore stop */

  /* v8 ignore next — deep AppInner rendering */
  const showAnimatedBg = isAnimatedBgRoute(location.pathname);

  // Page title for mobile header
  /* v8 ignore next 6 — deep AppInner rendering */
  const NAV_TITLES: Record<string, string> = {
    [AppRoutes.songs]: t('nav.songs'),
    [AppRoutes.suggestions]: t('nav.suggestions'),
    [AppRoutes.statistics]: t('nav.statistics'),
    [AppRoutes.settings]: t('nav.settings'),
    [AppRoutes.compete]: t('compete.title'),
    [AppRoutes.rivals]: fabSearch.rivalsActiveTab === 'song' ? t('rivals.tabSong') : t('rivals.tabLeaderboard'),
    [AppRoutes.leaderboards]: t('rankings.title'),
    [AppRoutes.shop]: t('nav.shop'),
  };
  const navTitle = NAV_TITLES[location.pathname] ?? null;

  // Hierarchical back-navigation fallback for detail pages only.
  // Tab routes (songs, suggestions, statistics, settings) never show a back button.
  /* v8 ignore start — deep AppInner: route-aware memo + animation IIFE */
  const backFallback = useMemo(() => {
    const path = location.pathname;
    const parts = path.split('/').filter(Boolean);
    if (parts[0] === 'songs' && parts.length === 4) return `/songs/${parts[1]}/${parts[2]}`;
    if (parts[0] === 'songs' && parts.length === 3) return `/songs/${parts[1]}`;
    if (parts[0] === 'songs' && parts.length === 2) return AppRoutes.songs;
    if (parts[0] === 'rivals' && parts.length === 4) return `/rivals/${parts[1]}`;
    if (parts[0] === 'rivals' && parts.length >= 2) return '/rivals';
    if (parts[0] === 'player' && parts.length === 3) return `/player/${parts[1]}`;
    if (parts[0] === 'player' && parts.length === 2) return AppRoutes.songs;
    if (parts[0] === 'leaderboards' && parts.length === 2) return AppRoutes.leaderboards;
    if (parts[0] === 'bands' && parts[1] === 'player' && parts[2]) return `/player/${parts[2]}`;
    return null;
  }, [location.pathname]);

  // Animate header only on first push into a detail stack
  const shouldAnimateHeader = (() => {
    if (!backFallback) {
      backShownRef.current = false;
      return false;
    }
    if (backShownRef.current) return false;
    backShownRef.current = true;
    return navType === 'PUSH';
  })();
  /* v8 ignore stop */

  const wideDesktop = !isMobile && isWideDesktop;
  const profileType = selectedProfile?.type ?? 'none';
  const emptyBandFilterLabel = t('common.filterBandType', 'Filter Band Type');
  const activeBandFilterAssignments = selectedProfile?.type === 'band' && appliedBandFilter?.bandId === selectedProfile.bandId
    ? appliedBandFilter.assignments
    : EMPTY_BAND_FILTER_ASSIGNMENTS;
  const selectedBandFilterInstruments = useMemo(
    () => activeBandFilterAssignments.map(assignment => assignment.instrument),
    [activeBandFilterAssignments],
  );
  const showBandFilterAction = shouldShowBandFilterAction(selectedProfile, location.pathname);
  const bandFilterLabel = getBandFilterActionLabel(selectedBandFilterInstruments, emptyBandFilterLabel);
  const handleBandFilterPress = useCallback(() => setBandFilterModalOpen(true), []);
  const handleApplyBandFilter = useCallback((assignments: BandInstrumentFilterAssignment[]) => {
    if (selectedProfile?.type !== 'band') return;
    setAppliedBandFilter({ bandId: selectedProfile.bandId, assignments });
    setBandFilterModalOpen(false);
  }, [selectedProfile]);
  const handleResetBandFilter = useCallback(() => {
    setAppliedBandFilter(null);
    setBandFilterModalOpen(false);
  }, []);
  useEffect(() => {
    if (selectedProfile?.type === 'band') return;
    setAppliedBandFilter(null);
    setBandFilterModalOpen(false);
  }, [selectedProfile?.type]);
  const bandFilterActionValue = useMemo<BandFilterActionContextValue>(() => ({
    visible: showBandFilterAction && !isMobile,
    label: bandFilterLabel,
    selectedInstruments: selectedBandFilterInstruments,
    onPress: handleBandFilterPress,
  }), [bandFilterLabel, handleBandFilterPress, isMobile, selectedBandFilterInstruments, showBandFilterAction]);
  const bandFilterFabActions: ActionItem[] = isMobile && showBandFilterAction
    ? [{ label: bandFilterLabel, icon: <IoFunnel size={Size.iconFab} />, onPress: handleBandFilterPress }]
    : [];
  const quickLinksActions = pageQuickLinks.hasPageQuickLinks && pageQuickLinks.pageQuickLinks
    ? [{
      label: getFabQuickLinksActionLabel(t),
      icon: <IoCompass size={Size.iconFab} />,
      onPress: () => pageQuickLinks.openPageQuickLinks(),
    }]
    : [];
  const withPageQuickLinks = (pageSpecificActions: ActionItem[], ...groups: ActionItem[][]) =>
    prependFabActionGroup(
      bandFilterFabActions,
      mergePageQuickLinksIntoFabGroups(quickLinksActions, pageSpecificActions, ...groups),
    );

  /** Shared FAB action group for player navigation (Find Player + Profile/Select + optionally Item Shop). */
  const playerActions = (includeShop = true) => [
    { label: t('common.searchAction'), icon: <IoSearch size={Size.iconFab} />, onPress: () => setSearchOpen(true) },
    player
      ? { label: player.displayName, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(AppRoutes.statistics) }
      : { label: t('common.selectPlayerProfile'), icon: <IoPerson size={Size.iconFab} />, onPress: () => setPlayerModalOpen(true) },
    ...(includeShop && isShopVisible ? [{ label: t('common.itemShop', 'Item Shop'), icon: <IoBagHandle size={Size.iconFab} />, onPress: () => navigate(AppRoutes.shop) }] : []),
  ];

  return (
    <BandFilterActionProvider value={bandFilterActionValue}>
    <PlayerDataProvider accountId={player?.accountId}>
    <div style={appStyles.shell}>
      <ScrollToTop />

      {/* v8 ignore start — sidebar callbacks tested via Sidebar.test / PinnedSidebar.test */}
      {!wideDesktop && (
        <Sidebar
          player={player}
          selectedProfile={selectedProfile}
          open={sidebarOpen}
          onClose={() => setSidebarOpen(false)}
          onDeselect={handleDeselect}
          onSelectPlayer={() => { setSidebarOpen(false); setPlayerModalOpen(true); }}
        />
      )}
      {/* v8 ignore stop */}

      {showAnimatedBg && <AnimatedBackground songs={songs} />}

      {/* v8 ignore start — mobile header conditional rendering */}
      {!isMobile && backFallback && (IS_IOS || IS_ANDROID || IS_PWA) && <BackLink key={location.pathname} fallback={backFallback} animate={shouldAnimateHeader} />}

        {isMobile ? (
          <MobileHeader
            navTitle={navTitle}
            backFallback={backFallback}
            shouldAnimate={shouldAnimateHeader}
            locationKey={location.pathname}
            songInstrument={songInstrument}
            isSongsRoute={location.pathname === AppRoutes.songs}
            onOpenSidebar={() => setSidebarOpen(true)}
          />
        ) : (
          <DesktopNav
            hasPlayer={!!player}
            profileType={profileType}
            onOpenSidebar={() => setSidebarOpen((o) => !o)}
            onProfileClick={handleProfileClick}
            onOpenSearch={() => setSearchOpen(true)}
            isWideDesktop={isWideDesktop}
          />
        )}
      {/* v8 ignore stop */}

      {/* v8 ignore start — wideDesktop layout tested via PinnedSidebar.test */}
      {wideDesktop ? (
        <WideDesktopLayout
          shellScrollRef={shellScrollRef}
          shellPortalRefCallback={shellPortalRefCallback}
          shellQuickLinksRailPortalRefCallback={shellQuickLinksRailPortalRefCallback}
          player={player}
          selectedProfile={selectedProfile}
          onDeselect={handleDeselect}
          onSelectPlayer={() => setPlayerModalOpen(true)}
        />
      ) : (
        <>
        <div ref={shellPortalRefCallback} style={appStyles.headerPortal} />
        <div ref={shellScrollRef} style={appStyles.scrollContainer}>
        <div style={appStyles.contentColumn}>
        <div id="main-content" style={appStyles.content}>
          <RoutesContent player={player} />
        </div>
        </div>
        </div>
        </>
      )}

      {/* v8 ignore start — mobile FAB configuration tested via MobileFabController + FloatingActionButton tests */}
      {isMobile && <BottomNav player={player} activeTab={activeTab} onTabClick={handleTabClick} />}
      {isMobile && location.pathname === AppRoutes.songs && (
        <FloatingActionButton
          mode="songs"
          defaultOpen
          placeholder={t('songs.searchPlaceholder')}
          actionGroups={withPageQuickLinks(
            [
              { label: t('common.sortSongs'), icon: <IoSwapVerticalSharp size={Size.iconFab} />, onPress: () => fabSearch.openSort() },
              ...(player ? [{ label: t('common.filterSongs'), icon: <IoFunnel size={Size.iconFab} />, onPress: () => fabSearch.openFilter() }] : []),
            ],
            playerActions(),
          )}
          onPress={() => {}}
        />
      )}
      {isMobile && location.pathname === AppRoutes.suggestions && (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [{ label: t('common.filterSuggestions'), icon: <IoFunnel size={Size.iconFab} />, onPress: () => fabSearch.openSuggestionsFilter() }],
            playerActions(),
          )}
          onPress={() => {}}
        />
      )}
      {isMobile && RoutePatterns.history.test(location.pathname) && (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [
              { label: t('common.sortPlayerScores'), icon: <IoSwapVerticalSharp size={Size.iconFab} />, onPress: () => fabSearch.openPlayerHistorySort() },
            ],
            playerActions(),
          )}
          onPress={() => {}}
        />
      )}
      {isMobile && RoutePatterns.songDetail.test(location.pathname) && (() => {
        const songIdMatch = location.pathname.match(/^\/songs\/([^/]+)$/);
        const currentSongId = songIdMatch?.[1];
        const currentShopUrl = currentSongId ? getShopUrl(currentSongId) : undefined;
        return (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            isNarrow && hasVisiblePathInstruments ? [{
              label: t('common.viewPaths'), icon: <IoFlash size={Size.iconFab} />, onPress: () => fabSearch.openPaths(),
            },
            ...(isShopVisible && currentShopUrl ? [{
              label: t('common.viewInItemShop', 'View in Item Shop'), icon: <IoBagHandle size={Size.iconFab} />,
              /* v8 ignore next */
              onPress: () => window.open(currentShopUrl, '_blank', 'noopener,noreferrer'),
            }] : [])] : [],
            playerActions(),
          )}
          onPress={() => {}}
        />
        );
      })()}
      {isMobile && location.pathname === AppRoutes.shop && (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            !isNarrowGrid ? [{
              label: fabSearch.shopViewMode === 'grid' ? t('common.listView', 'List View') : t('common.gridView', 'Grid View'),
              icon: fabSearch.shopViewMode === 'grid' ? <IoList size={Size.iconFab} /> : <IoGrid size={Size.iconFab} />,
              onPress: () => fabSearch.shopToggleView(),
            }] : [],
            playerActions(false),
          )}
          onPress={() => {}}
        />
      )}
      {isMobile && RoutePatterns.leaderboards.test(location.pathname) && (() => {
        const leaderboardActions = [
          ...(location.pathname === '/leaderboards/all' ? [{ label: t('rankings.changeInstrument'), icon: <IoMusicalNotes size={Size.iconFab} />, onPress: () => fabSearch.openLeaderboardInstrument() }] : []),
          ...(experimentalRanksEnabled ? [{ label: t('rankings.changeRanking'), icon: <IoOptions size={Size.iconFab} />, onPress: () => fabSearch.openLeaderboardMetric() }] : []),
        ];
        return (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            leaderboardActions,
            playerActions(),
          )}
          onPress={() => {}}
        />
        );
      })()}
      {isMobile && RoutePatterns.rivals.test(location.pathname) && (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [{
              label: fabSearch.rivalsActiveTab === 'song' ? t('rivals.tabLeaderboard') : t('rivals.tabSong'),
              icon: fabSearch.rivalsActiveTab === 'song' ? <IoTrophy size={Size.iconFab} /> : <IoMusicalNotes size={Size.iconFab} />,
              onPress: () => fabSearch.rivalsToggleTab(),
            }],
            playerActions(),
          )}
          onPress={() => {}}
        />
      )}
      {isMobile && RoutePatterns.playerBands.test(location.pathname) && (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [{ label: t('common.filterBands'), icon: <IoFunnel size={Size.iconFab} />, onPress: () => fabSearch.openBandFilter() }],
            playerActions(),
          )}
          onPress={() => {}}
        />
      )}
      {isMobile && location.pathname === AppRoutes.compete && (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [
              { label: t('compete.leaderboards'), icon: <IoTrophy size={Size.iconFab} />, onPress: () => navigate(AppRoutes.leaderboards) },
              { label: t('compete.rivals'), icon: <IoMusicalNotes size={Size.iconFab} />, onPress: () => navigate(AppRoutes.rivals) },
            ],
            playerActions(),
          )}
          onPress={() => {}}
        />
      )}
      {isMobile && RoutePatterns.rivalDetail.test(location.pathname) && !RoutePatterns.allRivals.test(location.pathname) && (() => {
        const rivalIdMatch = location.pathname.match(/^\/rivals\/([^/]+)$/);
        const currentRivalId = rivalIdMatch?.[1];
        const rivalName = new URLSearchParams(location.search).get('name');
        const profileLabel = rivalName ? t('common.viewNameProfile', { name: rivalName }) : t('common.viewProfile');
        return currentRivalId ? (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [{ label: profileLabel, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(AppRoutes.player(currentRivalId)) }],
            playerActions(),
          )}
          onPress={() => {}}
        />
        ) : null;
      })()}
      {isMobile && RoutePatterns.rivalry.test(location.pathname) && (() => {
        const rivalryIdMatch = location.pathname.match(/^\/rivals\/([^/]+)\/rivalry/);
        const currentRivalId = rivalryIdMatch?.[1];
        const rivalName = new URLSearchParams(location.search).get('name');
        const profileLabel = rivalName ? t('common.viewNameProfile', { name: rivalName }) : t('common.viewProfile');
        return currentRivalId ? (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [{ label: profileLabel, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(AppRoutes.player(currentRivalId)) }],
            playerActions(),
          )}
          onPress={() => {}}
        />
        ) : null;
      })()}
      {isMobile && location.pathname !== AppRoutes.songs && location.pathname !== AppRoutes.suggestions && location.pathname !== AppRoutes.shop && location.pathname !== AppRoutes.compete && !RoutePatterns.history.test(location.pathname) && !RoutePatterns.songDetail.test(location.pathname) && !RoutePatterns.leaderboards.test(location.pathname) && !RoutePatterns.rivals.test(location.pathname) && !RoutePatterns.rivalDetail.test(location.pathname) && !RoutePatterns.rivalry.test(location.pathname) && !RoutePatterns.playerBands.test(location.pathname) && (
        <FloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            pageQuickLinks.hasPageQuickLinks || fabSearch.playerPageSelect ? [
              ...(fabSearch.playerPageSelect
                ? [{ label: t('common.selectAsProfile', { name: fabSearch.playerPageSelect.displayName }), icon: <IoPersonAdd size={Size.iconFab} />, onPress: fabSearch.playerPageSelect.onSelect }]
                : []),
            ] : [],
            playerActions(),
          )}
          onPress={() => {}}
        />
      )}
      <MobilePlayerSearchModal
        visible={playerModalOpen}
        onClose={() => setPlayerModalOpen(false)}
        onSelect={handleSelect}
        player={player}
        onDeselect={handleDeselect}
        isMobile={isNarrow}
      />
      <SearchModal
        visible={searchOpen}
        onClose={() => setSearchOpen(false)}
        defaultTarget={settings.defaultSearchTarget}
      />
      <BandInstrumentFilterModal
        visible={bandFilterModalOpen && selectedProfile?.type === 'band'}
        selectedBand={selectedProfile?.type === 'band' ? selectedProfile : null}
        appliedAssignments={activeBandFilterAssignments}
        onCancel={() => setBandFilterModalOpen(false)}
        onApply={handleApplyBandFilter}
        onReset={handleResetBandFilter}
      />
      {showChangelog && <ChangelogModal onDismiss={dismissChangelog} onExitComplete={() => setChangelogDismissed(true)} />}
      {showDeselectConfirm && (
        <ConfirmAlert
          title={t('common.deselectConfirmTitle')}
          message={t('common.deselectConfirmMessage')}
          onNo={() => setShowDeselectConfirm(false)}
          onYes={confirmDeselect}
          onExitComplete={() => setShowDeselectConfirm(false)}
        />
      )}
      {/* v8 ignore stop */}
    </div>
    </PlayerDataProvider>
    </BandFilterActionProvider>
  );
}

/* v8 ignore start — scroll restoration utility */
function ScrollToTop() {
  const location = useLocation();
  const { pathname } = location;
  const preserveShellScrollKey = (location.state as PreserveShellScrollState | null)?.preserveShellScrollKey;
  const scrollContainerRef = useScrollContainer();
  useEffect(() => {
    if ('scrollRestoration' in history) {
      history.scrollRestoration = 'manual';
    }
  }, []);
  useEffect(() => {
    if (preserveShellScrollKey && !consumedPreserveShellScrollKeys.has(preserveShellScrollKey)) {
      consumedPreserveShellScrollKeys.add(preserveShellScrollKey);
      return;
    }
    // On browser refresh, always scroll to top — page exemptions only apply to in-app navigation
    if (!IS_PAGE_RELOAD) {
      if (pathname === AppRoutes.suggestions || pathname === AppRoutes.songs) return;
      // Song detail pages manage their own scroll restoration
      if (RoutePatterns.songDetail.test(pathname)) return;
    }
    scrollContainerRef.current?.scrollTo(0, 0);
  }, [pathname, preserveShellScrollKey, scrollContainerRef]);
  return null;
}
/* v8 ignore stop */

