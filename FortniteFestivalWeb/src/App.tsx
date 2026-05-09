import { HashRouter, Routes, Route, Navigate, useLocation, useNavigate, useNavigationType } from 'react-router-dom';
import { IoCompass, IoPerson, IoPersonAdd, IoSwapVerticalSharp, IoFunnel, IoFlash, IoGrid, IoList, IoOptions, IoMusicalNotes, IoTrophy } from 'react-icons/io5';
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
function RoutesContent({ player, selectedProfile }: { player: TrackedPlayer | null; selectedProfile: SelectedProfile | null }) {
  const selectedBand = selectedProfile?.type === 'band' ? selectedProfile : null;
  return (
    <Suspense fallback={<SuspenseFallback />}>
    <Routes>
      <Route path="/" element={<Navigate to={AppRoutes.songs} replace />} />
      <Route path="/songs" element={<SongsPage />} />
      <Route path="/songs/:songId" element={<ErrorBoundary fallback={<RouteErrorFallback />}><SongDetailPage /></ErrorBoundary>} />
      <Route path="/songs/:songId/bands/:bandType" element={<ErrorBoundary fallback={<RouteErrorFallback />}><SongBandLeaderboardPage /></ErrorBoundary>} />
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
      <Route path="/statistics" element={player
        ? <ErrorBoundary fallback={<RouteErrorFallback />}><PlayerPage accountId={player.accountId} /></ErrorBoundary>
        : selectedBand
          ? <ErrorBoundary fallback={<RouteErrorFallback />}><BandPage statisticsBand={selectedBand} /></ErrorBoundary>
          : <Navigate to={AppRoutes.songs} replace />}
      />
      {player ? (
        <Route path="/suggestions" element={<ErrorBoundary fallback={<RouteErrorFallback />}><SuggestionsPage accountId={player.accountId} /></ErrorBoundary>} />
      ) : (
        <Route path="/suggestions" element={<Navigate to={AppRoutes.songs} replace />} />
      )}
      <Route path="/shop" element={<ErrorBoundary fallback={<RouteErrorFallback />}><ShopPage /></ErrorBoundary>} />
      <Route path="/leaderboards" element={<ErrorBoundary fallback={<RouteErrorFallback />}><LeaderboardsOverviewPage /></ErrorBoundary>} />
      <Route path="/leaderboards/all" element={<ErrorBoundary fallback={<RouteErrorFallback />}><FullRankingsPage /></ErrorBoundary>} />
      <Route path="/leaderboards/bands/:bandType" element={<ErrorBoundary fallback={<RouteErrorFallback />}><BandRankingsPage /></ErrorBoundary>} />
      <Route path="/bands/player/:accountId" element={<ErrorBoundary fallback={<RouteErrorFallback />}><PlayerBandsPage /></ErrorBoundary>} />
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
import { useProximityGlow } from './hooks/ui/useProximityGlow';
import BottomNav from './components/shell/mobile/BottomNav';
import Sidebar from './components/shell/desktop/Sidebar';
import DesktopNav from './components/shell/desktop/DesktopNav';
import PinnedSidebar from './components/shell/desktop/PinnedSidebar';
import FloatingActionButton, { type ActionItem } from './components/shell/fab/FloatingActionButton';
import MobilePlayerSearchModal from './components/shell/mobile/MobilePlayerSearchModal';
import SearchModal from './components/search/SearchModal';
import MobileNotificationsModal, { type MobileNotification } from './components/notifications/MobileNotificationsModal';
import { getNotificationDestination } from './components/notifications/notificationDestination';
import { useNotificationFreshnessState } from './components/notifications/notificationFreshnessState';
import { useNotificationSeenState } from './components/notifications/notificationSeenState';
import { NotificationFeedWebSocketBridge, useProfileNotificationsFeed } from './components/notifications/useProfileNotificationsFeed';
import type { SearchTarget } from './types/search';
import { clearSongDetailCache, clearLeaderboardCache, clearPlayerPageCache } from './api/pageCache';
import { IS_IOS, IS_ANDROID, IS_PWA, IS_PAGE_RELOAD } from '@festival/ui-utils';
import ChangelogModal from './components/modals/ChangelogModal';
import ConfirmAlert from './components/modals/ConfirmAlert';
import BandInstrumentFilterModal, { type BandInstrumentFilterApplyPayload, type BandInstrumentFilterAssignment } from './pages/band/modals/BandInstrumentFilterModal';
import type { AppliedBandComboFilter } from './types/bandFilter';
import { APP_VERSION } from './hooks/data/useVersions';
import { changelogHash } from './changelog';
import ErrorBoundary from './components/page/ErrorBoundary';
import SuspenseFallback from './components/common/SuspenseFallback';
import RouteErrorFallback from './components/page/RouteErrorFallback';
import { createPreserveShellScrollState, type PreserveShellScrollState } from './utils/quietNavigation';
import { getBandFilterActionLabel } from './utils/bandFilterDisplay';
import { bandTypeLabel } from './utils/bandTypes';
import { saveLeaderboardRankBy } from './utils/leaderboardSettings';
import { getProfileClickDestination } from './utils/profileNavigation';
import {
  clearAppliedBandFilter,
  isBandFilterForSelectedProfile,
  readAppliedBandFilterForSelectedProfile,
  writeAppliedBandFilter,
} from './state/bandFilter';
import { writeSelectedProfile } from './state/selectedProfile';

const consumedPreserveShellScrollKeys = new Set<string>();
const NOTIFICATIONS_VALIDATION_TOKEN = 'notifications-open';
const EMPTY_NOTIFICATIONS_VALIDATION_TOKEN = 'notifications-empty';
const MOCK_NOTIFICATION_SOURCE_VERSION = 'mock-source-2026-05-09';

function hasWindowValidationToken(token: string): boolean {
  if (typeof window === 'undefined') return false;
  const value = new URLSearchParams(window.location.search).get('validation') ?? '';
  return value.split(/[,:;]/).some(part => part.trim() === token);
}
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { queryClient } from './api/queryClient';
import { Routes as AppRoutes, RoutePatterns } from './routes';
import { FirstRunProvider, useFirstRunContext } from './contexts/FirstRunContext';
import { ScrollContainerProvider, useShellRefs, useScrollContainer, HEADER_PORTAL_HEIGHT_VAR } from './contexts/ScrollContainerContext';
import { FeatureFlagsProvider } from './contexts/FeatureFlagsContext';

export { getProfileClickDestination, getStatisticsNavigationPath } from './utils/profileNavigation';

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

export function getEmptyBandFilterActionLabel(selectedProfile: SelectedProfile | null, t: TFunction): string {
  if (selectedProfile?.type === 'band') return bandTypeLabel(selectedProfile.bandType, t);
  return t('common.filterBandType', 'Filter Band Type');
}

export function shouldShowBandFilterAction(selectedProfile: SelectedProfile | null, pathname: string): boolean {
  return selectedProfile?.type === 'band' && pathname !== AppRoutes.settings;
}

export function prependFabActionGroup(leadingActions: ActionItem[], actionGroups: ActionItem[][]): ActionItem[][] {
  return leadingActions.length > 0 ? [leadingActions, ...actionGroups] : actionGroups;
}

export function getBackFallback(pathname: string, search = ''): string | null {
  const parts = pathname.split('/').filter(Boolean);
  if (parts[0] === 'songs' && parts.length === 4) return `/songs/${parts[1]}/${parts[2]}`;
  if (parts[0] === 'songs' && parts.length === 3) return `/songs/${parts[1]}`;
  if (parts[0] === 'songs' && parts.length === 2) return AppRoutes.songs;
  if (parts[0] === 'rivals' && parts.length === 4) return `/rivals/${parts[1]}`;
  if (parts[0] === 'rivals' && parts.length >= 2) return AppRoutes.rivals;
  if (parts[0] === 'player' && parts.length === 3) return `/player/${parts[1]}`;
  if (parts[0] === 'player' && parts.length === 2) return AppRoutes.songs;
  if (parts[0] === 'leaderboards' && parts.length === 2) return AppRoutes.leaderboards;
  if (parts[0] === 'bands' && parts[1] === 'player' && parts[2]) return `/player/${parts[2]}`;
  if (parts[0] === 'bands' && (parts.length === 1 || (parts.length === 2 && parts[1] !== 'player'))) {
    const params = new URLSearchParams(search);
    const accountId = params.get('accountId');
    if (accountId) return AppRoutes.playerBands(accountId);
    const bandType = params.get('bandType');
    if (bandType) return AppRoutes.bandRankings(bandType);
    return AppRoutes.leaderboards;
  }
  return null;
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

function MobileFloatingActionButton(props: React.ComponentProps<typeof FloatingActionButton>) {
  const actionGroups = props.actionGroups ?? [];
  const hasActions = actionGroups.some(group => group.length > 0);
  if (!props.defaultOpen && !hasActions && !props.directAction) return null;
  return <FloatingActionButton {...props} actionGroups={actionGroups} />;
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
              <RoutesContent player={player} selectedProfile={selectedProfile} />
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
  const useEmptyNotificationMock = hasWindowValidationToken(EMPTY_NOTIFICATIONS_VALIDATION_TOKEN);
  const useNotificationMockData = hasWindowValidationToken(NOTIFICATIONS_VALIDATION_TOKEN) || useEmptyNotificationMock;
  const notificationFeed = useProfileNotificationsFeed(selectedProfile, songs, {
    useMockData: useNotificationMockData,
    useEmptyMock: useEmptyNotificationMock,
    mockSourceVersion: MOCK_NOTIFICATION_SOURCE_VERSION,
  });
  const notificationIds = notificationFeed.notificationIds;
  const hasNotifications = notificationFeed.notifications.length > 0;
  const notificationFeedReadyForHeader = useNotificationMockData || notificationFeed.status !== 'loading';
  const notificationFeedKey = notificationFeed.feedKey;
  const { unreadNotificationIds, unreadCount, markNotificationsSeen } = useNotificationSeenState(notificationFeedKey, notificationIds);
  const { newNotificationIds } = useNotificationFreshnessState(notificationFeedKey, notificationIds, notificationFeed.sourceVersion);
  const { settings } = useSettings();

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
  const {
    scrollRef: shellScrollRef,
    portalRefCallback: shellPortalRefCallback,
    quickLinksRailPortalRefCallback: shellQuickLinksRailPortalRefCallback,
  } = useShellRefs();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [playerModalOpen, setPlayerModalOpen] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [notificationsOpen, setNotificationsOpen] = useState(false);
  const validationOpenedNotificationsRef = useRef(false);
  const [searchTargetOverride, setSearchTargetOverride] = useState<SearchTarget | null>(null);
  const [bandFilterModalOpen, setBandFilterModalOpen] = useState(false);
  const [appliedBandFilter, setAppliedBandFilter] = useState<AppliedBandComboFilter | null>(() => readAppliedBandFilterForSelectedProfile(selectedProfile));
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

  const openSearch = useCallback((defaultTarget?: SearchTarget) => {
    setSearchTargetOverride(defaultTarget ?? null);
    setSearchOpen(true);
  }, []);

  const closeSearch = useCallback(() => {
    setSearchOpen(false);
    setSearchTargetOverride(null);
  }, []);

  const shouldAutoOpenNotifications = hasWindowValidationToken(NOTIFICATIONS_VALIDATION_TOKEN) || useEmptyNotificationMock;
  const canOpenNotifications = selectedProfile != null && notificationFeedReadyForHeader;
  const handleOpenNotifications = useCallback(() => setNotificationsOpen(true), []);

  useEffect(() => {
    if (validationOpenedNotificationsRef.current) return;
    if (import.meta.env.MODE !== 'e2e') return;
    if (!shouldAutoOpenNotifications) return;
    validationOpenedNotificationsRef.current = true;
    setNotificationsOpen(true);
  }, [shouldAutoOpenNotifications]);

  useEffect(() => {
    if (selectedProfile || useNotificationMockData) return;
    setNotificationsOpen(false);
  }, [selectedProfile, useNotificationMockData]);

  const handleNotificationOpen = useCallback((notification: MobileNotification) => {
    const destination = getNotificationDestination(notification);
    if (!destination) return;

    if (destination.rankBy) saveLeaderboardRankBy(destination.rankBy);
    if (destination.bandProfile) writeSelectedProfile(destination.bandProfile);
    if (destination.bandFilter) setAppliedBandFilter(writeAppliedBandFilter(destination.bandFilter));

    setNotificationsOpen(false);
    navigate(destination.path, destination.state ? { state: destination.state } : undefined);
  }, [navigate]);

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
    clearAppliedBandFilter();
    setAppliedBandFilter(null);
    setBandFilterModalOpen(false);
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
    clearAppliedBandFilter();
    setAppliedBandFilter(null);
    setBandFilterModalOpen(false);
    if (selectedProfile?.type === 'band') {
      clearPlayer();
      return;
    }
    setShowDeselectConfirm(true);
  }, [clearPlayer, selectedProfile?.type]);
  const confirmDeselect = useCallback(() => {
    clearAppliedBandFilter();
    setAppliedBandFilter(null);
    setBandFilterModalOpen(false);
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

  const handleMobileHeaderProfileAction = useCallback(() => {
    if (!selectedProfile) {
      openSearch('players');
      return;
    }
    handleProfileClick();
  }, [handleProfileClick, openSearch, selectedProfile]);
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
    return getBackFallback(location.pathname, location.search);
  }, [location.pathname, location.search]);

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
  const mobileHeaderProfileLabel = selectedProfile?.type === 'player'
    ? t('common.viewNameProfile', { name: selectedProfile.displayName })
    : selectedProfile?.type === 'band'
      ? t('bandList.viewBand', { names: selectedProfile.displayName })
      : t('common.selectPlayerProfile');
  const emptyBandFilterLabel = getEmptyBandFilterActionLabel(selectedProfile, t);
  const selectedBandIdentity = selectedProfile?.type === 'band'
    ? `${selectedProfile.bandId}|${selectedProfile.bandType}|${selectedProfile.teamKey}`
    : selectedProfile?.type ?? 'none';
  const activeBandFilter = isBandFilterForSelectedProfile(appliedBandFilter, selectedProfile)
    ? appliedBandFilter
    : null;
  const activeBandFilterAssignments = activeBandFilter
    ? activeBandFilter.assignments
    : EMPTY_BAND_FILTER_ASSIGNMENTS;
  const selectedBandFilterInstruments = useMemo(
    () => activeBandFilterAssignments.map(assignment => assignment.instrument),
    [activeBandFilterAssignments],
  );
  const showBandFilterAction = shouldShowBandFilterAction(selectedProfile, location.pathname);
  const bandFilterLabel = getBandFilterActionLabel(selectedBandFilterInstruments, emptyBandFilterLabel);
  const handleBandFilterPress = useCallback(() => setBandFilterModalOpen(true), []);
  const handleApplyBandFilter = useCallback((payload: BandInstrumentFilterApplyPayload) => {
    if (selectedProfile?.type !== 'band') return;
    const nextFilter = writeAppliedBandFilter({
      bandId: selectedProfile.bandId,
      bandType: selectedProfile.bandType,
      teamKey: selectedProfile.teamKey,
      comboId: payload.comboId,
      assignments: payload.assignments,
      configurations: payload.configurations,
    });
    setAppliedBandFilter(nextFilter);
    setBandFilterModalOpen(false);
  }, [selectedProfile]);
  const handleResetBandFilter = useCallback(() => {
    clearAppliedBandFilter();
    setAppliedBandFilter(null);
    setBandFilterModalOpen(false);
  }, []);
  useEffect(() => {
    if (!appliedBandFilter || isBandFilterForSelectedProfile(appliedBandFilter, selectedProfile)) return;
    clearAppliedBandFilter();
    setAppliedBandFilter(null);
    setBandFilterModalOpen(false);
  }, [appliedBandFilter, selectedBandIdentity, selectedProfile]);
  const bandFilterActionValue = useMemo<BandFilterActionContextValue>(() => ({
    visible: showBandFilterAction && !isMobile,
    label: bandFilterLabel,
    selectedInstruments: selectedBandFilterInstruments,
    appliedFilter: activeBandFilter,
    onPress: handleBandFilterPress,
  }), [activeBandFilter, bandFilterLabel, handleBandFilterPress, isMobile, selectedBandFilterInstruments, showBandFilterAction]);
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
  const showMobileFab = isMobile && !notificationsOpen;

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
            profileType={profileType}
            profileLabel={mobileHeaderProfileLabel}
            onProfileAction={handleMobileHeaderProfileAction}
            onOpenSearch={() => openSearch()}
            onOpenNotifications={canOpenNotifications ? handleOpenNotifications : undefined}
            hasNotifications={hasNotifications}
            notificationCount={unreadCount}
          />
        ) : (
          <DesktopNav
            hasPlayer={!!player}
            profileType={profileType}
            profileLabel={mobileHeaderProfileLabel}
            onOpenSidebar={() => setSidebarOpen((o) => !o)}
            onProfileClick={handleProfileClick}
            onOpenSearch={() => openSearch()}
            onOpenNotifications={canOpenNotifications ? handleOpenNotifications : undefined}
            hasNotifications={hasNotifications}
            notificationCount={unreadCount}
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
          <RoutesContent player={player} selectedProfile={selectedProfile} />
        </div>
        </div>
        </div>
        </>
      )}

      {/* v8 ignore start — mobile FAB configuration tested via MobileFabController + FloatingActionButton tests */}
      {isMobile && <BottomNav player={player} selectedProfile={selectedProfile} activeTab={activeTab} onTabClick={handleTabClick} />}
      {showMobileFab && location.pathname === AppRoutes.songs && (
        <MobileFloatingActionButton
          mode="songs"
          defaultOpen
          placeholder={t('songs.searchPlaceholder')}
          actionGroups={withPageQuickLinks(
            [
              { label: t('common.sortSongs'), icon: <IoSwapVerticalSharp size={Size.iconFab} />, onPress: () => fabSearch.openSort() },
              ...(player ? [{ label: t('common.filterSongs'), icon: <IoFunnel size={Size.iconFab} />, onPress: () => fabSearch.openFilter() }] : []),
            ],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && location.pathname === AppRoutes.suggestions && (
        <MobileFloatingActionButton
          mode="players"
          icon={<IoFunnel size={Size.iconFab} />}
          ariaLabel={t('common.filterSuggestions')}
          directAction
          onPress={() => fabSearch.openSuggestionsFilter()}
        />
      )}
      {showMobileFab && location.pathname === AppRoutes.settings && pageQuickLinks.hasPageQuickLinks && (
        <MobileFloatingActionButton
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          directAction
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && (location.pathname === AppRoutes.statistics || RoutePatterns.player.test(location.pathname)) && pageQuickLinks.hasPageQuickLinks && (
        <MobileFloatingActionButton
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          directAction
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && RoutePatterns.history.test(location.pathname) && (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [
              { label: t('common.sortPlayerScores'), icon: <IoSwapVerticalSharp size={Size.iconFab} />, onPress: () => fabSearch.openPlayerHistorySort() },
            ],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && RoutePatterns.songDetail.test(location.pathname) && (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            isNarrow && hasVisiblePathInstruments ? [{
              label: t('common.viewPaths'), icon: <IoFlash size={Size.iconFab} />, onPress: () => fabSearch.openPaths(),
            }] : [],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && location.pathname === AppRoutes.shop && (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            !isNarrowGrid ? [{
              label: fabSearch.shopViewMode === 'grid' ? t('common.listView', 'List View') : t('common.gridView', 'Grid View'),
              icon: fabSearch.shopViewMode === 'grid' ? <IoList size={Size.iconFab} /> : <IoGrid size={Size.iconFab} />,
              onPress: () => fabSearch.shopToggleView(),
            }] : [],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && RoutePatterns.leaderboards.test(location.pathname) && (() => {
        const leaderboardActions = [
          ...(location.pathname === '/leaderboards/all' ? [{ label: t('rankings.changeInstrument'), icon: <IoMusicalNotes size={Size.iconFab} />, onPress: () => fabSearch.openLeaderboardInstrument() }] : []),
          { label: t('rankings.changeRanking'), icon: <IoOptions size={Size.iconFab} />, onPress: () => fabSearch.openLeaderboardMetric() },
        ];
        return (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            leaderboardActions,
          )}
          onPress={() => {}}
        />
        );
      })()}
      {showMobileFab && RoutePatterns.rivals.test(location.pathname) && (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [{
              label: fabSearch.rivalsActiveTab === 'song' ? t('rivals.tabLeaderboard') : t('rivals.tabSong'),
              icon: fabSearch.rivalsActiveTab === 'song' ? <IoTrophy size={Size.iconFab} /> : <IoMusicalNotes size={Size.iconFab} />,
              onPress: () => fabSearch.rivalsToggleTab(),
            }],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && RoutePatterns.playerBands.test(location.pathname) && (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [{ label: t('common.filterBands'), icon: <IoFunnel size={Size.iconFab} />, onPress: () => fabSearch.openBandFilter() }],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && location.pathname === AppRoutes.compete && (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [
              { label: t('compete.leaderboards'), icon: <IoTrophy size={Size.iconFab} />, onPress: () => navigate(AppRoutes.leaderboards) },
              { label: t('compete.rivals'), icon: <IoMusicalNotes size={Size.iconFab} />, onPress: () => navigate(AppRoutes.rivals) },
            ],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && RoutePatterns.rivalDetail.test(location.pathname) && !RoutePatterns.allRivals.test(location.pathname) && (() => {
        const rivalIdMatch = location.pathname.match(/^\/rivals\/([^/]+)$/);
        const currentRivalId = rivalIdMatch?.[1];
        const rivalName = new URLSearchParams(location.search).get('name');
        const profileLabel = rivalName ? t('common.viewNameProfile', { name: rivalName }) : t('common.viewProfile');
        return currentRivalId ? (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [{ label: profileLabel, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(AppRoutes.player(currentRivalId)) }],
          )}
          onPress={() => {}}
        />
        ) : null;
      })()}
      {showMobileFab && RoutePatterns.rivalry.test(location.pathname) && (() => {
        const rivalryIdMatch = location.pathname.match(/^\/rivals\/([^/]+)\/rivalry/);
        const currentRivalId = rivalryIdMatch?.[1];
        const rivalName = new URLSearchParams(location.search).get('name');
        const profileLabel = rivalName ? t('common.viewNameProfile', { name: rivalName }) : t('common.viewProfile');
        return currentRivalId ? (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            [{ label: profileLabel, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(AppRoutes.player(currentRivalId)) }],
          )}
          onPress={() => {}}
        />
        ) : null;
      })()}
      {showMobileFab && location.pathname !== AppRoutes.songs && location.pathname !== AppRoutes.suggestions && location.pathname !== AppRoutes.statistics && location.pathname !== AppRoutes.settings && location.pathname !== AppRoutes.shop && location.pathname !== AppRoutes.compete && !RoutePatterns.history.test(location.pathname) && !RoutePatterns.player.test(location.pathname) && !RoutePatterns.songDetail.test(location.pathname) && !RoutePatterns.leaderboards.test(location.pathname) && !RoutePatterns.rivals.test(location.pathname) && !RoutePatterns.rivalDetail.test(location.pathname) && !RoutePatterns.rivalry.test(location.pathname) && !RoutePatterns.playerBands.test(location.pathname) && (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            pageQuickLinks.hasPageQuickLinks || fabSearch.playerPageSelect ? [
              ...(fabSearch.playerPageSelect
                ? [{ label: t('common.selectAsProfile', { name: fabSearch.playerPageSelect.displayName }), icon: <IoPersonAdd size={Size.iconFab} />, onPress: fabSearch.playerPageSelect.onSelect }]
                : []),
            ] : [],
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
        onClose={closeSearch}
        defaultTarget={searchTargetOverride ?? settings.defaultSearchTarget}
      />
      <MobileNotificationsModal
        visible={notificationsOpen}
        onClose={() => setNotificationsOpen(false)}
        presentation={isMobile ? 'mobileModal' : 'desktopDrawer'}
        notifications={notificationFeed.notifications}
        unreadNotificationIds={unreadNotificationIds}
        newNotificationIds={newNotificationIds}
        notificationsGenerated={notificationFeed.generationStatus === 'generated'}
        onNotificationsSeen={markNotificationsSeen}
        onNotificationOpen={handleNotificationOpen}
      />
      {selectedProfile && !useNotificationMockData && <NotificationFeedWebSocketBridge profile={selectedProfile} />}
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

