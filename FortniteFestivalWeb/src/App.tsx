import { HashRouter, Routes, Route, useLocation, useNavigate, useNavigationType } from 'react-router-dom';
import { IoCompass, IoPerson, IoPersonAdd, IoSwapVerticalSharp, IoFunnel, IoFlash, IoGrid, IoList, IoOptions, IoMusicalNotes, IoTrophy, IoBagHandle, IoPeople, IoSearch } from 'react-icons/io5';
import { useEffect, useLayoutEffect, useState, useMemo, useRef, useCallback, Suspense, lazy } from 'react';
import { useTranslation } from 'react-i18next';
import type { TFunction } from 'i18next';
import { FestivalProvider, useFestival } from './contexts/FestivalContext';
import { SettingsProvider } from './contexts/SettingsContext';
import { FeatureFlagsProvider, useFeatureFlagsState } from './contexts/FeatureFlagsContext';
import { ShopProvider } from './contexts/ShopContext';
import { useTrackedPlayer, type TrackedPlayer } from './hooks/data/useTrackedPlayer';
import { useSelectedProfileNameRefresh } from './hooks/data/useSelectedProfileNameRefresh';
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
const loadSuggestionsPage = () => import('./pages/suggestions/SuggestionsPage');
const SuggestionsPage = lazy(loadSuggestionsPage);
const ManualPage = lazy(() => import('./pages/manual/ManualPage'));
const SettingsPage = lazy(() => import('./pages/settings/SettingsPage'));
const LicensesPage = lazy(() => import('./pages/settings/LicensesPage'));
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
const NotFoundPage = lazy(() => import('./pages/NotFoundPage'));
const AnimatedBackground = lazy(() => import('./components/shell/AnimatedBackground').then(module => ({
  default: module.AnimatedBackground,
})));
const ProximityGlowRuntime = lazy(() => import('./components/shell/ProximityGlowRuntime'));
/* v8 ignore stop */
import { Size, Layout, QUERY_NARROW_GRID } from '@festival/theme';

/** Shared route tree used by both mobile and wide-desktop layouts. */
function RoutesContent({ player, selectedProfile }: { player: TrackedPlayer | null; selectedProfile: SelectedProfile | null }) {
  const selectedBand = selectedProfile?.type === 'band' ? selectedProfile : null;
  const hasPlayer = !!player;
  const hasSelection = hasPlayer || !!selectedBand;
  return (
    <Suspense fallback={<SuspenseFallback />}>
    <Routes>
      <Route path={AppRoutes.root} element={<RedirectToSongs />} />
      <Route path={AppRoutes.songs} element={<RouteBoundary><SongsPage /></RouteBoundary>} />
      <Route path="/songs/:songId" element={<RouteBoundary><SongDetailPage /></RouteBoundary>} />
      <Route path="/songs/:songId/bands/:bandType" element={<RouteBoundary><SongBandLeaderboardPage /></RouteBoundary>} />
      <Route path="/songs/:songId/:instrument" element={<RouteBoundary><LeaderboardPage /></RouteBoundary>} />
      <Route path="/songs/:songId/:instrument/history" element={<RouteBoundary><PlayerHistoryPage /></RouteBoundary>} />
      <Route path="/player/:accountId" element={<RouteBoundary><PlayerPage /></RouteBoundary>} />
      <Route
        path={AppRoutes.rivals}
        element={(
          <RequirePlayer hasPlayer={hasPlayer}>
            <RouteBoundary><RivalsPage /></RouteBoundary>
          </RequirePlayer>
        )}
      />
      <Route
        path={AppRoutes.allRivalsRoot}
        element={(
          <RequirePlayer hasPlayer={hasPlayer}>
            <RouteBoundary><AllRivalsPage /></RouteBoundary>
          </RequirePlayer>
        )}
      />
      <Route
        path="/rivals/:rivalId"
        element={(
          <RequirePlayer hasPlayer={hasPlayer}>
            <RouteBoundary><RivalDetailPage /></RouteBoundary>
          </RequirePlayer>
        )}
      />
      <Route
        path="/rivals/:rivalId/rivalry"
        element={(
          <RequirePlayer hasPlayer={hasPlayer}>
            <RouteBoundary><RivalCategoryPage /></RouteBoundary>
          </RequirePlayer>
        )}
      />
      <Route
        path={AppRoutes.statistics}
        element={(
          <RequireSelection hasSelection={hasSelection}>
            {player
              ? <RouteBoundary><PlayerPage accountId={player.accountId} /></RouteBoundary>
              : selectedBand
                ? <RouteBoundary><BandPage statisticsBand={selectedBand} /></RouteBoundary>
                : null}
          </RequireSelection>
        )}
      />
      <Route
        path={AppRoutes.suggestions}
        element={(
          <RequireSelection hasSelection={hasSelection}>
            <RouteBoundary>
              <SuggestionsPage accountId={player?.accountId} selectedBand={selectedBand} />
            </RouteBoundary>
          </RequireSelection>
        )}
      />
      <Route path={AppRoutes.shop} element={<RouteBoundary><ShopPage /></RouteBoundary>} />
      <Route path={AppRoutes.manual} element={<ManualRouteElement />} />
      <Route path={AppRoutes.leaderboards} element={<RouteBoundary><LeaderboardsOverviewPage /></RouteBoundary>} />
      <Route path={AppRoutes.fullRankingsRoot} element={<RouteBoundary><FullRankingsPage /></RouteBoundary>} />
      <Route path="/leaderboards/bands/:bandType" element={<RouteBoundary><BandRankingsPage /></RouteBoundary>} />
      <Route path="/bands/player/:accountId" element={<RouteBoundary><PlayerBandsPage /></RouteBoundary>} />
      <Route path={AppRoutes.bands} element={<RouteBoundary><BandPage /></RouteBoundary>} />
      <Route path="/bands/:bandId" element={<RouteBoundary><BandPage /></RouteBoundary>} />
      <Route
        path={AppRoutes.compete}
        element={(
          <RequirePlayer hasPlayer={hasPlayer}>
            <RouteBoundary><CompetePage /></RouteBoundary>
          </RequirePlayer>
        )}
      />
      <Route path={AppRoutes.settings} element={<RouteBoundary><SettingsPage /></RouteBoundary>} />
      <Route path={AppRoutes.settingsLicenses} element={<RouteBoundary><LicensesPage /></RouteBoundary>} />
      <Route path="*" element={<RouteBoundary><NotFoundPage /></RouteBoundary>} />
    </Routes>
    </Suspense>
  );
}
import { appStyles } from './appStyles';
import { resetSongSettingsForDeselect, loadSongSettings, SONG_SETTINGS_CHANGED_EVENT } from './utils/songSettings';
import BackLink from './components/shell/mobile/BackLink';
import MobileHeader from './components/shell/mobile/MobileHeader';
import { HEADER_NOTIFICATION_SWAP_FADE_MS, type HeaderNotificationVisualState } from './components/shell/HeaderActions';
import { FabSearchProvider, useFabSearch } from './contexts/FabSearchContext';
import { PageQuickLinksProvider, usePageQuickLinksController } from './contexts/PageQuickLinksContext';
import { PageReadyProvider, usePageReady } from './contexts/PageReadyContext';
import { BandFilterActionProvider, type BandFilterActionContextValue } from './contexts/BandFilterActionContext';
import { FabVisibilityProvider } from './contexts/FabVisibilityContext';
import { SearchQueryProvider } from './contexts/SearchQueryContext';
import { useSettings, visibleInstruments, visiblePathInstruments } from './contexts/SettingsContext';
import { useShopState } from './hooks/data/useShopState';
import BottomNav from './components/shell/mobile/BottomNav';
import Sidebar from './components/shell/desktop/Sidebar';
import DesktopNav from './components/shell/desktop/DesktopNav';
import PinnedSidebar from './components/shell/desktop/PinnedSidebar';
import MobileFloatingActionButton from './components/shell/fab/MobileFloatingActionButton';
import ComboInstrumentFabAccessory from './components/shell/fab/ComboInstrumentFabAccessory';
import type { ActionItem } from './components/shell/fab/FloatingActionButton';
import { InstrumentIcon } from './components/display/InstrumentIcons';
import LazyModalBoundary from './components/common/LazyModalBoundary';
import {
  LazyBandInstrumentFilterModal,
  LazyChangelogModal,
  LazyConfirmAlert,
  LazyMobileNotificationsModal,
  LazySearchModal,
  isBandInstrumentFilterModalLoaded,
  isChangelogModalLoaded,
  isConfirmAlertLoaded,
  isMobileNotificationsModalLoaded,
  isSearchModalLoaded,
  loadBandInstrumentFilterModal,
  loadChangelogModal,
  loadConfirmAlert,
  loadMobileNotificationsModal,
  loadSearchModal,
  preloadBandInstrumentFilterModal,
  preloadMobileNotificationsModal,
  preloadSearchModal,
} from './components/lazy/secondaryControls';
import { filterSurfaceNotifications } from './components/notifications/notificationSurface';
import type { MobileNotification } from './components/notifications/notificationTypes';
import { getNotificationDestination } from './components/notifications/notificationDestination';
import { useNotificationFreshnessState } from './components/notifications/notificationFreshnessState';
import { notificationFeedKeyForProfile, useNotificationSeenState } from './components/notifications/notificationSeenState';
import { NotificationFeedWebSocketBridge, useProfileNotificationsFeed } from './components/notifications/useProfileNotificationsFeed';
import type { SearchTarget } from './types/search';
import { IS_IOS, IS_ANDROID, IS_PWA, IS_PAGE_RELOAD } from '@festival/ui-utils';
import { DEFAULT_INSTRUMENT, SERVER_INSTRUMENT_KEYS, serverInstrumentLabel, type ServerInstrumentKey } from '@festival/core/api';
import type { AppliedBandComboFilter, BandInstrumentFilterApplyPayload, BandInstrumentFilterAssignment } from './types/bandFilter';
import { APP_VERSION } from './hooks/data/useVersions';
import { changelogHash } from './changelogHash';
import ErrorBoundary from './components/page/ErrorBoundary';
import SuspenseFallback from './components/common/SuspenseFallback';
import RouteBoundary from './components/page/RouteBoundary';
import { RedirectToSongs, RequirePlayer, RequireSelection } from './components/page/RouteGuards';
import type { PreserveShellScrollState } from './utils/quietNavigation';
import { getBandFilterActionLabel } from './utils/bandFilterDisplay';
import { bandTypeLabel } from './utils/bandTypes';
import { saveLeaderboardRankBy } from './utils/leaderboardSettings';
import { getPlayerProfileRoute, getProfileClickDestination } from './utils/profileNavigation';
import {
  clearAppliedBandFilter,
  isBandFilterForSelectedProfile,
  readAppliedBandFilterForSelectedProfile,
  writeAppliedBandFilter,
} from './state/bandFilter';
import { writeSelectedProfile } from './state/selectedProfile';
import { queryClient } from './api/queryClient';
import { invalidateLeaderboardData } from './api/queryPolicy';
import { isKnownRoutePath, normalizeRoutePathname, Routes as AppRoutes, RoutePatterns } from './routes';
import {
  markCurrentSuggestionsScrollRestorable,
} from './pages/suggestions/suggestionsSessionCache';
import { FirstRunProvider, useFirstRunContext } from './contexts/FirstRunContext';
import { ScrollContainerProvider, useShellRefs, useScrollContainer, HEADER_PORTAL_HEIGHT_VAR } from './contexts/ScrollContainerContext';
import { useTapDiagnostics } from './diagnostics/useTapDiagnostics';
import anim from './styles/animations.module.css';
import { RouteAccessibility, RouteMain } from './components/shell/RouteAccessibility';

const consumedPreserveShellScrollKeys = new Set<string>();
const LEADERBOARD_INSTRUMENT_ACTION_ICON_SIZE = 32;
const NOTIFICATIONS_VALIDATION_TOKEN = 'notifications-open';
const EMPTY_NOTIFICATIONS_VALIDATION_TOKEN = 'notifications-empty';
const MOCK_NOTIFICATION_SOURCE_VERSION = 'mock-source-2026-05-09';
const PROFILE_SEARCH_TARGETS: readonly SearchTarget[] = ['players', 'bands'];
const PLAYER_BANDS_ACTIVE_FILTER_GROUPS = new Set(['duos', 'trios', 'quads']);

function ManualRouteElement() {
  const { flags, resolved } = useFeatureFlagsState();
  if (!resolved) return <SuspenseFallback />;
  if (!flags.appManual) return <RedirectToSongs />;
  return <RouteBoundary><ManualPage /></RouteBoundary>;
}

type SearchModalConfig = {
  availableTargets?: readonly SearchTarget[];
  placeholderKey?: string;
};

const PROFILE_SEARCH_CONFIG: SearchModalConfig = {
  availableTargets: PROFILE_SEARCH_TARGETS,
  placeholderKey: 'search.placeholders.playersBands',
};
const NOTIFICATION_SWAP_PRIME_MS = 32;

function hasWindowValidationToken(token: string): boolean {
  if (typeof window === 'undefined') return false;
  const value = new URLSearchParams(window.location.search).get('validation') ?? '';
  return value.split(/[,:;]/).some(part => part.trim() === token);
}

export { getProfileClickDestination, getStatisticsNavigationPath } from './utils/profileNavigation';

export default function App() {
  return (
    <FeatureFlagsProvider>
    <SettingsProvider>
      <FestivalProvider>
        <ShopProvider>
        <FirstRunProvider>
        <FabSearchProvider>
        <PageQuickLinksProvider>
          <SearchQueryProvider>
            <HashRouter>
              <PageReadyProvider>
              <ScrollContainerProvider>
              <AppShell />
              </ScrollContainerProvider>
              </PageReadyProvider>
            </HashRouter>
          </SearchQueryProvider>
        </PageQuickLinksProvider>
        </FabSearchProvider>
        </FirstRunProvider>
        </ShopProvider>
      </FestivalProvider>
    </SettingsProvider>
    </FeatureFlagsProvider>
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
  return selectedProfile?.type === 'band'
    && !pathname.startsWith(AppRoutes.settings)
    && pathname !== AppRoutes.manual
    && !RoutePatterns.songBandLeaderboard.test(pathname)
    && !pathname.startsWith('/leaderboards/bands/');
}

export function prependFabActionGroup(leadingActions: ActionItem[], actionGroups: ActionItem[][]): ActionItem[][] {
  return leadingActions.length > 0 ? [leadingActions, ...actionGroups] : actionGroups;
}

export function getBackFallback(pathname: string, search = ''): string | null {
  const parts = pathname.split('/').filter(Boolean);
  if (pathname === AppRoutes.settingsLicenses) return AppRoutes.settings;
  if (parts[0] === 'songs' && parts.length === 4) return `/songs/${parts[1]}/${parts[2]}`;
  if (parts[0] === 'songs' && parts.length === 3) return `/songs/${parts[1]}`;
  if (parts[0] === 'songs' && parts.length === 2) return AppRoutes.songs;
  if (parts[0] === 'rivals' && parts.length === 4) return `/rivals/${parts[1]}`;
  if (parts[0] === 'rivals' && parts.length >= 2) return AppRoutes.rivals;
  if (parts[0] === 'player' && parts.length === 3) return `/player/${parts[1]}`;
  if (parts[0] === 'player' && parts.length === 2) return AppRoutes.songs;
  if (parts[0] === 'leaderboards' && parts[1] === 'bands' && parts.length === 3) return AppRoutes.leaderboards;
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

function resolveLeaderboardInstrument(search: string): ServerInstrumentKey {
  const value = new URLSearchParams(search).get('instrument');
  return value && SERVER_INSTRUMENT_KEYS.includes(value as ServerInstrumentKey)
    ? value as ServerInstrumentKey
    : DEFAULT_INSTRUMENT;
}

function getSongDetailId(pathname: string): string | undefined {
  const match = pathname.match(/^\/songs\/([^/]+)$/);
  if (!match?.[1]) return undefined;
  try {
    return decodeURIComponent(match[1]);
  } catch {
    return undefined;
  }
}

const ANIMATED_BG_ROUTES = new Set<string>([AppRoutes.root, AppRoutes.songs, AppRoutes.suggestions, AppRoutes.statistics, AppRoutes.manual, AppRoutes.settings, AppRoutes.settingsLicenses, AppRoutes.shop, AppRoutes.compete, AppRoutes.leaderboards]);
/* v8 ignore start — route detection helper */
function isAnimatedBgRoute(pathname: string) {
  return ANIMATED_BG_ROUTES.has(pathname) || RoutePatterns.player.test(pathname) || pathname.startsWith('/rivals') || pathname.startsWith('/leaderboards') || pathname.startsWith('/bands');
}
/* v8 ignore stop */

function WideDesktopLayout({
  shellScrollRef,
  shellPortalRefCallback,
  shellQuickLinksRailPortalRefCallback,
  player,
  selectedProfile,
  onDeselect,
  onSelectPlayer,
  routeTitle,
  fallbackHeading,
  pageHeaderLabel,
}: {
  shellScrollRef: React.RefObject<HTMLDivElement | null>;
  shellPortalRefCallback: (el: HTMLDivElement | null) => void;
  shellQuickLinksRailPortalRefCallback: (el: HTMLDivElement | null) => void;
  player: TrackedPlayer | null;
  selectedProfile: ReturnType<typeof useTrackedPlayer>['profile'];
  onDeselect: () => void;
  onSelectPlayer: () => void;
  routeTitle: string;
  fallbackHeading: boolean;
  pageHeaderLabel: string;
}) {
  return (
    <div style={appStyles.bodySection}>
      {/* Scroll container starts below the header overlay — content can never reach it.
         top is driven by a CSS custom property updated outside React to avoid re-render cascades. */}
      <div data-testid="app-scroll-container" ref={shellScrollRef} style={{ ...appStyles.scrollContainerFull, top: `var(${HEADER_PORTAL_HEIGHT_VAR}, 0px)` }}>
        <div style={appStyles.scrollContentRow}>
          <div style={appStyles.sidebarGutter} />
          <div style={appStyles.centerColumn}>
            <RouteMain
              routeTitle={routeTitle}
              fallbackHeading={fallbackHeading}
              style={{ ...appStyles.content, ...appStyles.contentPinned }}
            >
              <RoutesContent player={player} selectedProfile={selectedProfile} />
            </RouteMain>
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
        <div
          ref={shellPortalRefCallback}
          role="region"
          aria-label={pageHeaderLabel}
          style={appStyles.headerPortalWide}
        />
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
function AppShell() {
  const { t } = useTranslation();
  const { profile: selectedProfile, player, clearPlayer } = useTrackedPlayer();
  useSelectedProfileNameRefresh(selectedProfile);
  const { state: { songs } } = useFestival();
  const useEmptyNotificationMock = hasWindowValidationToken(EMPTY_NOTIFICATIONS_VALIDATION_TOKEN);
  const useNotificationMockData = hasWindowValidationToken(NOTIFICATIONS_VALIDATION_TOKEN) || useEmptyNotificationMock;
  const [notificationRequestProfile, setNotificationRequestProfile] = useState<SelectedProfile | null>(selectedProfile);
  const [notificationHeaderVisualState, setNotificationHeaderVisualState] = useState<HeaderNotificationVisualState>('icon');
  const notificationPendingProfileRef = useRef<SelectedProfile | null>(selectedProfile);
  const notificationSwapTimersRef = useRef<number[]>([]);
  const selectedNotificationFeedKey = useMemo(() => notificationFeedKeyForProfile(selectedProfile), [selectedProfile]);
  const requestedNotificationFeedKey = useMemo(() => notificationFeedKeyForProfile(notificationRequestProfile), [notificationRequestProfile]);
  const clearNotificationSwapTimers = useCallback(() => {
    notificationSwapTimersRef.current.forEach(timer => window.clearTimeout(timer));
    notificationSwapTimersRef.current = [];
  }, []);
  const queueNotificationSwapTimer = useCallback((callback: () => void, delay: number) => {
    const timer = window.setTimeout(() => {
      notificationSwapTimersRef.current = notificationSwapTimersRef.current.filter(activeTimer => activeTimer !== timer);
      callback();
    }, delay);
    notificationSwapTimersRef.current.push(timer);
  }, []);
  const notificationFeed = useProfileNotificationsFeed(notificationRequestProfile, songs, {
    useMockData: useNotificationMockData,
    useEmptyMock: useEmptyNotificationMock,
    mockSourceVersion: MOCK_NOTIFICATION_SOURCE_VERSION,
  });
  const { settings } = useSettings();
  const notificationIds = notificationFeed.notificationIds;
  const notificationFeedReadyForHeader = notificationFeed.status !== 'loading';
  const notificationFeedKey = notificationFeed.feedKey;
  const notificationRequestMatchesSelection = selectedProfile != null && selectedNotificationFeedKey === requestedNotificationFeedKey;
  const notificationFeedAuthoritative = useNotificationMockData
    ? notificationFeed.status === 'ready'
    : (
        notificationRequestMatchesSelection
        && notificationFeed.status === 'ready'
        && notificationFeed.generationStatus === 'generated'
      );
  const { unreadNotificationIds, markNotificationsSeen } = useNotificationSeenState(notificationFeedKey, notificationIds, {
    isCurrentFeedLoaded: notificationFeedAuthoritative,
  });
  const { newNotificationIds } = useNotificationFreshnessState(notificationFeedKey, notificationIds, notificationFeed.sourceVersion, {
    isCurrentFeedLoaded: notificationFeedAuthoritative,
  });
  const notificationInstrumentFilter = useMemo(() => {
    if (notificationRequestProfile?.type !== 'player') return null;
    return new Set(visibleInstruments(settings));
  }, [notificationRequestProfile?.type, settings]);
  const surfaceNotifications = useMemo(
    () => filterSurfaceNotifications(notificationFeed.notifications, {
      visibleInstruments: notificationInstrumentFilter,
      enableExperimentalRanks: settings.enableExperimentalRanks,
    }),
    [notificationFeed.notifications, notificationInstrumentFilter, settings.enableExperimentalRanks],
  );
  const surfaceNotificationIds = useMemo(
    () => new Set(surfaceNotifications.map(notification => notification.notificationGuid)),
    [surfaceNotifications],
  );
  const surfaceUnreadNotificationIds = useMemo(
    () => new Set(Array.from(unreadNotificationIds).filter(id => surfaceNotificationIds.has(id))),
    [surfaceNotificationIds, unreadNotificationIds],
  );
  const surfaceNewNotificationIds = useMemo(
    () => new Set(Array.from(newNotificationIds).filter(id => surfaceNotificationIds.has(id))),
    [newNotificationIds, surfaceNotificationIds],
  );
  const hasNotifications = surfaceNotifications.length > 0;
  const surfaceUnreadCount = surfaceUnreadNotificationIds.size;

  usePlayerBandsPrefetch(player?.accountId);

  const location = useLocation();
  const leaderboardInstrument = useMemo(() => resolveLeaderboardInstrument(location.search), [location.search]);
  const isMobile = useIsMobileChrome();
  const isNarrow = useIsMobile();
  const isNarrowGrid = useMediaQuery(QUERY_NARROW_GRID);
  const isWideDesktop = useIsWideDesktop();
  const hasVisiblePathInstruments = visiblePathInstruments(settings).length > 0;
  const fabSearch = useFabSearch();
  const { isShopVisible, isShopHighlighted, isLeavingTomorrow, isShopNew, getShopUrl } = useShopState();
  const pageQuickLinks = usePageQuickLinksController();
  const {
    scrollRef: shellScrollRef,
    portalRefCallback: shellPortalRefCallback,
    quickLinksRailPortalRefCallback: shellQuickLinksRailPortalRefCallback,
  } = useShellRefs();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [notificationsOpen, setNotificationsOpen] = useState(false);
  const validationOpenedNotificationsRef = useRef(false);
  const [searchConfig, setSearchConfig] = useState<SearchModalConfig | null>(null);
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
  const showChangelog = hasNewChangelog && !changelogDismissed && !activeCarouselKey;
  /* v8 ignore start — modal dismiss callback */
  const dismissChangelog = useCallback(() => {
    localStorage.setItem(CHANGELOG_STORAGE_KEY, JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));
  }, []);
  const closeChangelog = useCallback(() => {
    dismissChangelog();
    setChangelogDismissed(true);
  }, [dismissChangelog]);
  /* v8 ignore stop */
  const navigate = useNavigate();
  const navType = useNavigationType();

  const openSearch = useCallback((config?: SearchModalConfig) => {
    preloadSearchModal();
    setSearchConfig(config ?? null);
    setSearchOpen(true);
  }, []);
  const openProfileSearch = useCallback(() => openSearch(PROFILE_SEARCH_CONFIG), [openSearch]);

  const closeSearch = useCallback(() => {
    setSearchOpen(false);
    setSearchConfig(null);
  }, []);

  const shouldAutoOpenNotifications = hasWindowValidationToken(NOTIFICATIONS_VALIDATION_TOKEN) || useEmptyNotificationMock;
  const notificationHeaderBusy = notificationHeaderVisualState !== 'icon';
  const canOpenNotifications = selectedProfile != null && notificationRequestMatchesSelection && notificationFeedReadyForHeader && !notificationHeaderBusy;
  const handleOpenNotifications = useCallback(() => {
    preloadMobileNotificationsModal();
    setNotificationsOpen(true);
  }, []);

  useEffect(() => () => clearNotificationSwapTimers(), [clearNotificationSwapTimers]);

  useEffect(() => {
    if (!selectedProfile) {
      clearNotificationSwapTimers();
      notificationPendingProfileRef.current = null;
      setNotificationHeaderVisualState('icon');
      setNotificationRequestProfile(null);
      return;
    }

    if (!notificationRequestProfile) {
      clearNotificationSwapTimers();
      notificationPendingProfileRef.current = selectedProfile;
      setNotificationHeaderVisualState('icon');
      setNotificationRequestProfile(selectedProfile);
      return;
    }

    if (selectedNotificationFeedKey === requestedNotificationFeedKey) return;

    clearNotificationSwapTimers();
    notificationPendingProfileRef.current = selectedProfile;
    setNotificationsOpen(false);
    setNotificationHeaderVisualState('icon');
    queueNotificationSwapTimer(() => {
      setNotificationHeaderVisualState('iconOut');
      queueNotificationSwapTimer(() => {
        setNotificationHeaderVisualState('spinnerIn');
        queueNotificationSwapTimer(() => {
          const pendingProfile = notificationPendingProfileRef.current;
          if (!pendingProfile) return;
          setNotificationRequestProfile(pendingProfile);
          setNotificationHeaderVisualState('spinner');
        }, HEADER_NOTIFICATION_SWAP_FADE_MS);
      }, HEADER_NOTIFICATION_SWAP_FADE_MS);
    }, NOTIFICATION_SWAP_PRIME_MS);
  }, [
    clearNotificationSwapTimers,
    notificationRequestProfile,
    queueNotificationSwapTimer,
    requestedNotificationFeedKey,
    selectedNotificationFeedKey,
    selectedProfile,
  ]);

  useEffect(() => {
    if (notificationHeaderVisualState !== 'spinner') return;
    if (!notificationRequestProfile || !notificationRequestMatchesSelection) return;
    if (!notificationFeedReadyForHeader) return;

    queueNotificationSwapTimer(() => {
      setNotificationHeaderVisualState('spinnerOut');
      queueNotificationSwapTimer(() => setNotificationHeaderVisualState('icon'), HEADER_NOTIFICATION_SWAP_FADE_MS);
    }, NOTIFICATION_SWAP_PRIME_MS);
  }, [
    notificationFeedReadyForHeader,
    notificationHeaderVisualState,
    notificationRequestMatchesSelection,
    notificationRequestProfile,
    queueNotificationSwapTimer,
  ]);

  useEffect(() => {
    if (validationOpenedNotificationsRef.current) return;
    if (import.meta.env.MODE !== 'e2e') return;
    if (!shouldAutoOpenNotifications) return;
    if (!notificationFeedReadyForHeader) return;
    validationOpenedNotificationsRef.current = true;
    setNotificationsOpen(true);
  }, [notificationFeedReadyForHeader, shouldAutoOpenNotifications]);

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

  // Invalidate server-filtered leaderboard data without discarding navigation state.
  const filterRef = useRef({ e: settings.filterInvalidScores, l: settings.filterInvalidScoresLeeway });
  /* v8 ignore start — deep AppInner: filter change cache invalidation */
  useEffect(() => {
    const prev = filterRef.current;
    if (prev.e !== settings.filterInvalidScores || prev.l !== settings.filterInvalidScoresLeeway) {
      filterRef.current = { e: settings.filterInvalidScores, l: settings.filterInvalidScoresLeeway };
      // Invalidate leaderboard queries (server-side filtering required).
      // Player queries are NOT invalidated — the precomputed response includes
      // minLeeway + validScores, so the client handles all leeway values locally.
      void invalidateLeaderboardData(queryClient);
      /* v8 ignore stop */
    }
  }, [settings.filterInvalidScores, settings.filterInvalidScoresLeeway]);

  // --- Per-tab stack (mobile only) ---
  const { activeTab, handleTabClick } = useTabNavigation();

  const [showDeselectConfirm, setShowDeselectConfirm] = useState(false);
  useTapDiagnostics({
    pathname: location.pathname,
    search: location.search,
    hash: typeof window !== 'undefined' ? window.location.hash : '',
    activeTab,
    isMobile,
    isNarrow,
    sidebarOpen,
    searchOpen,
    notificationsOpen,
    bandFilterModalOpen,
    showChangelog,
    showDeselectConfirm,
    notificationHeaderVisualState,
    canOpenNotifications,
    activeCarouselKey,
    fabReady: {
      songs: fabSearch.songsActionsReady,
      suggestions: fabSearch.suggestionsActionsReady,
      playerHistory: fabSearch.playerHistoryActionsReady,
      songDetail: fabSearch.songDetailActionsReady,
      shop: fabSearch.shopActionsReady,
      leaderboardMetric: fabSearch.leaderboardMetricReady,
      leaderboardInstrument: fabSearch.leaderboardInstrumentReady,
      rivalsToggleTab: fabSearch.rivalsToggleTabReady,
      rivalsFindRival: fabSearch.rivalsFindRivalReady,
      band: fabSearch.bandActionsReady,
      playerQuickLinks: fabSearch.hasPlayerQuickLinks,
      pageQuickLinks: pageQuickLinks.hasPageQuickLinks,
    },
    player: player ? { accountId: player.accountId, displayName: player.displayName } : null,
    selectedProfile: selectedProfile ? { type: selectedProfile.type, displayName: selectedProfile.displayName } : null,
  });
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
    else if (dest === 'search') openProfileSearch();
    else navigate(dest);
  }, [navigate, openProfileSearch, player, selectedProfile]);

  const handleMobileHeaderProfileAction = useCallback(() => {
    if (!selectedProfile) {
      openProfileSearch();
      return;
    }
    handleProfileClick();
  }, [handleProfileClick, openProfileSearch, selectedProfile]);
  const profileSelectionIntent = getProfileClickDestination(player, selectedProfile) === 'search'
    ? preloadSearchModal
    : undefined;
  /* v8 ignore stop */

  /* v8 ignore start — deep AppInner: instrument sync event listener */
  const [songInstrument, setSongInstrument] = useState(() => loadSongSettings().instrument);
  useEffect(() => {
    const sync = () => setSongInstrument(loadSongSettings().instrument);
    window.addEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
    return () => window.removeEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
  }, []);
  /* v8 ignore stop */

  const routePathname = normalizeRoutePathname(location.pathname);
  const showAnimatedBg = isAnimatedBgRoute(routePathname);

  const NAV_TITLES: Record<string, string> = {
    [AppRoutes.songs]: t('nav.songs'),
    [AppRoutes.suggestions]: t('nav.suggestions'),
    [AppRoutes.statistics]: t('nav.statistics'),
    [AppRoutes.manual]: t('nav.manual'),
    [AppRoutes.settings]: t('nav.settings'),
    [AppRoutes.compete]: t('compete.title'),
    [AppRoutes.rivals]: fabSearch.rivalsActiveTab === 'song' ? t('rivals.tabSong') : t('rivals.tabLeaderboard'),
    [AppRoutes.leaderboards]: t('rankings.title'),
    [AppRoutes.shop]: t('nav.shop'),
  };
  const knownRoute = isKnownRoutePath(routePathname);
  const navTitle = routePathname === AppRoutes.statistics
    ? (player?.displayName ?? (selectedProfile?.type === 'band' ? selectedProfile.displayName : t('nav.statistics')))
    : (NAV_TITLES[routePathname] ?? (knownRoute ? null : t('apiError.notFound')));
  const mainLabel = navTitle ?? t('common.brandName');
  const fallbackRouteHeading = navTitle !== null;

  // Hierarchical back-navigation fallback for detail pages only.
  // Tab routes (songs, suggestions, statistics, settings) never show a back button.
  /* v8 ignore start — deep AppInner: route-aware memo + animation IIFE */
  const backFallback = useMemo(() => {
    return getBackFallback(routePathname, location.search);
  }, [routePathname, location.search]);

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
      : t('common.selectProfile');
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
  const showBandFilterAction = shouldShowBandFilterAction(selectedProfile, routePathname);
  const bandFilterLabel = getBandFilterActionLabel(selectedBandFilterInstruments, emptyBandFilterLabel);
  const bandFilterActive = selectedBandFilterInstruments.length > 0;
  const bandFilterIconAccessory = bandFilterActive
    ? <ComboInstrumentFabAccessory instruments={selectedBandFilterInstruments} />
    : undefined;
  const handleBandFilterPress = useCallback(() => {
    preloadBandInstrumentFilterModal();
    setBandFilterModalOpen(true);
  }, []);
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
    appliedAssignments: activeBandFilterAssignments,
    onPress: handleBandFilterPress,
    onIntent: preloadBandInstrumentFilterModal,
    onApplyFilter: handleApplyBandFilter,
    onResetFilter: handleResetBandFilter,
  }), [activeBandFilter, activeBandFilterAssignments, bandFilterLabel, handleApplyBandFilter, handleBandFilterPress, handleResetBandFilter, isMobile, selectedBandFilterInstruments, showBandFilterAction]);
  const leaderboardsSideActions: ActionItem[] = isMobile && routePathname === AppRoutes.leaderboards
    ? [
      ...(showBandFilterAction ? [{
      label: bandFilterLabel,
      active: bandFilterActive,
      iconOnly: true,
      icon: <IoFunnel size={Size.iconFab} />,
      iconAccessory: bandFilterIconAccessory,
      onPress: handleBandFilterPress,
      onIntent: preloadBandInstrumentFilterModal,
    }] : []),
      ...(fabSearch.leaderboardMetricReady ? [{ label: t('rankings.changeRanking'), active: fabSearch.leaderboardMetricActive, iconOnly: true, icon: <IoOptions size={Size.iconFab} />, onPress: () => fabSearch.openLeaderboardMetric() }] : []),
    ]
    : [];
  const leaderboardBandComboFabActions: ActionItem[] = fabSearch.leaderboardBandComboReady ? [{
    label: fabSearch.leaderboardBandComboLabel || t('bandComboFilter.actionLabel'),
    active: fabSearch.leaderboardBandComboActive,
    icon: <IoFunnel size={Size.iconFab} />,
    iconAccessory: fabSearch.leaderboardBandComboActive
      ? <ComboInstrumentFabAccessory instruments={fabSearch.leaderboardBandComboInstruments} />
      : undefined,
    onPress: () => fabSearch.openLeaderboardBandCombo(),
  }] : [];
  const bandFilterFabActions: ActionItem[] = isMobile && showBandFilterAction && routePathname !== AppRoutes.leaderboards
    ? [{ label: bandFilterLabel, active: bandFilterActive, icon: <IoFunnel size={Size.iconFab} />, iconAccessory: bandFilterIconAccessory, onPress: handleBandFilterPress, onIntent: preloadBandInstrumentFilterModal }]
    : [];
  const statisticsSideActions: ActionItem[] = isMobile && routePathname === AppRoutes.statistics && !player && selectedProfile?.type === 'band'
    ? bandFilterFabActions.map(action => ({ ...action, iconOnly: true }))
    : [];
  const playerSelectSideActions: ActionItem[] = isMobile && RoutePatterns.player.test(routePathname) && fabSearch.playerPageSelect
    ? [{ label: t('common.selectPlayerName', { name: fabSearch.playerPageSelect.displayName }), icon: <IoPersonAdd size={Size.iconFab} />, onPress: fabSearch.playerPageSelect.onSelect }]
    : [];
  const bandSelectSideActions: ActionItem[] = isMobile && RoutePatterns.bands.test(routePathname) && !RoutePatterns.playerBands.test(routePathname) && fabSearch.bandPageSelect
    ? [{ label: t('common.selectBand'), icon: <IoPeople size={Size.iconFab} />, onPress: fabSearch.bandPageSelect.onSelect }]
    : [];
  const suggestionsFabActive = bandFilterActive || fabSearch.suggestionsFilterActive;
  const quickLinksActions = pageQuickLinks.hasPageQuickLinks && pageQuickLinks.pageQuickLinks
    ? [{
      label: getFabQuickLinksActionLabel(t),
      icon: <IoCompass size={Size.iconFab} />,
      onPress: () => pageQuickLinks.openPageQuickLinks(),
    }]
    : [];
  const songDetailId = getSongDetailId(routePathname);
  const songDetailShopUrl = songDetailId ? getShopUrl(songDetailId) : undefined;
  const showSongDetailShopAction = !!songDetailId && isShopVisible && !!songDetailShopUrl;
  const songDetailSideActions: ActionItem[] = songDetailId ? [
    ...(showSongDetailShopAction ? [{
      label: t('common.itemShop', 'Item Shop'),
      icon: <IoBagHandle size={Size.iconFab} />,
      href: songDetailShopUrl,
      target: '_blank',
      rel: 'noopener noreferrer',
      tone: isShopHighlighted(songDetailId) ? 'pulse' as const : 'accent' as const,
      className: isShopHighlighted(songDetailId) ? (isLeavingTomorrow(songDetailId) ? anim.shopBreatheRed : isShopNew(songDetailId) ? anim.shopBreatheGold : anim.shopBreathe) : undefined,
      onPress: () => {},
    }] : []),
    ...(hasVisiblePathInstruments && fabSearch.songDetailActionsReady ? [{
      label: t('common.viewPaths'),
      icon: <IoFlash size={Size.iconFab} />,
      onPress: () => fabSearch.openPaths(),
    }] : []),
  ] : [];
  const withPageQuickLinks = (pageSpecificActions: ActionItem[], ...groups: ActionItem[][]) =>
    prependFabActionGroup(
      bandFilterFabActions,
      mergePageQuickLinksIntoFabGroups(quickLinksActions, pageSpecificActions, ...groups),
    );
  const playerBandsFilterGroup = useMemo(() => new URLSearchParams(location.search).get('group') ?? 'all', [location.search]);
  const playerBandsFilterActive = PLAYER_BANDS_ACTIVE_FILTER_GROUPS.has(playerBandsFilterGroup);
  const showMobileFab = isMobile && !notificationsOpen;
  // Pages publish their content-ready state via `useSetPageReady` (defaults to
  // true for pages that don't opt in). The FAB row's `ready` prop AND's with
  // this so the FAB reveals in lockstep with the page's own staggered content.
  const pageReady = usePageReady();
  const isStatisticsRoute = routePathname === AppRoutes.statistics;
  const isPlayerDetailRoute = RoutePatterns.player.test(routePathname);
  const playerDetailFabReady = isStatisticsRoute
    ? pageReady
    : pageReady && playerSelectSideActions.length > 0;
  const bandDetailFabReady = pageReady && (
    bandSelectSideActions.length > 0
    || bandFilterFabActions.length > 0
    || pageQuickLinks.hasPageQuickLinks
  );
  const isBandRankingsRoute = RoutePatterns.bandRankings.test(routePathname);
  const leaderboardBandComboSideActions = leaderboardBandComboFabActions.map(action => ({ ...action, iconOnly: true }));
  const showBandRankingsMetricFab = isBandRankingsRoute && settings.enableExperimentalRanks && fabSearch.leaderboardMetricReady;
  const bandRankingsComboOnlyAction = !showBandRankingsMetricFab ? leaderboardBandComboFabActions[0] : undefined;

  return (
    <BandFilterActionProvider value={bandFilterActionValue}>
    <PlayerDataProvider accountId={player?.accountId}>
    <FabVisibilityProvider mobileFabHidden={!showMobileFab}>
    <>
    {showAnimatedBg && (
      <ErrorBoundary fallback={null}>
      <Suspense fallback={null}>
        <AnimatedBackground songs={songs} />
      </Suspense>
      </ErrorBoundary>
    )}
    {!settings.disableLightTrails && (
      <ErrorBoundary fallback={null}>
        <Suspense fallback={null}>
          <ProximityGlowRuntime enabled />
        </Suspense>
      </ErrorBoundary>
    )}
    <div style={appStyles.shell}>
      <RouteAccessibility
        pathname={location.pathname}
        titleOverride={navTitle}
        navigationType={navType}
        skipLabel={t('common.skipToContent')}
      />
      <ScrollToTop layoutKey={wideDesktop ? 'wide' : 'standard'} />

      {/* v8 ignore start — sidebar callbacks tested via Sidebar.test / PinnedSidebar.test */}
      {!wideDesktop && (
        <Sidebar
          player={player}
          selectedProfile={selectedProfile}
          open={sidebarOpen}
          onClose={() => setSidebarOpen(false)}
          onDeselect={handleDeselect}
          onSelectPlayer={() => { setSidebarOpen(false); openProfileSearch(); }}
        />
      )}
      {/* v8 ignore stop */}

      {/* v8 ignore start — mobile header conditional rendering */}
      {!isMobile && backFallback && (IS_IOS || IS_ANDROID || IS_PWA) && <BackLink key={routePathname} fallback={backFallback} animate={shouldAnimateHeader} />}

        {isMobile ? (
          <MobileHeader
            navTitle={navTitle}
            backFallback={backFallback}
            shouldAnimate={shouldAnimateHeader}
            locationKey={routePathname}
            songInstrument={songInstrument}
            isSongsRoute={routePathname === AppRoutes.songs}
            onOpenSidebar={() => setSidebarOpen(true)}
            profileType={profileType}
            profileLabel={mobileHeaderProfileLabel}
            onProfileAction={handleMobileHeaderProfileAction}
            onProfileIntent={profileSelectionIntent}
            onOpenSearch={() => openSearch()}
            onSearchIntent={preloadSearchModal}
            onOpenNotifications={canOpenNotifications ? handleOpenNotifications : undefined}
            onNotificationsIntent={canOpenNotifications ? preloadMobileNotificationsModal : undefined}
            hasNotifications={hasNotifications}
            notificationCount={surfaceUnreadCount}
            notificationVisualState={notificationHeaderVisualState}
          />
        ) : (
          <DesktopNav
            hasPlayer={!!player}
            profileType={profileType}
            profileLabel={mobileHeaderProfileLabel}
            onOpenSidebar={() => setSidebarOpen((o) => !o)}
            onProfileClick={handleProfileClick}
            onProfileIntent={profileSelectionIntent}
            onOpenSearch={() => openSearch()}
            onSearchIntent={preloadSearchModal}
            onOpenNotifications={canOpenNotifications ? handleOpenNotifications : undefined}
            onNotificationsIntent={canOpenNotifications ? preloadMobileNotificationsModal : undefined}
            hasNotifications={hasNotifications}
            notificationCount={surfaceUnreadCount}
            notificationVisualState={notificationHeaderVisualState}
            isWideDesktop={isWideDesktop}
          />
        )}
      {/* v8 ignore stop */}

      {wideDesktop ? (
        <WideDesktopLayout
          shellScrollRef={shellScrollRef}
          shellPortalRefCallback={shellPortalRefCallback}
          shellQuickLinksRailPortalRefCallback={shellQuickLinksRailPortalRefCallback}
          player={player}
          selectedProfile={selectedProfile}
          onDeselect={handleDeselect}
          onSelectPlayer={openProfileSearch}
          routeTitle={mainLabel}
          fallbackHeading={fallbackRouteHeading}
          pageHeaderLabel={t('common.pageHeader')}
        />
      ) : (
        <>
        <div
          ref={shellPortalRefCallback}
          role="region"
          aria-label={t('common.pageHeader')}
          style={appStyles.headerPortal}
        />
        <div data-testid="app-scroll-container" ref={shellScrollRef} style={appStyles.scrollContainer}>
        <div style={appStyles.contentColumn}>
        <RouteMain
          routeTitle={mainLabel}
          fallbackHeading={fallbackRouteHeading}
          style={appStyles.content}
        >
          <RoutesContent player={player} selectedProfile={selectedProfile} />
        </RouteMain>
        </div>
        </div>
        </>
      )}

      {isMobile && <BottomNav player={player} selectedProfile={selectedProfile} activeTab={activeTab} onTabClick={handleTabClick} />}
      {showMobileFab && routePathname === AppRoutes.suggestions && fabSearch.suggestionsActionsReady && (
        <MobileFloatingActionButton
          pageKey="suggestions"
          ready={pageReady}
          mode="players"
          ariaLabel={t('common.filterSuggestions')}
          icon={<IoFunnel size={Size.iconFab} />}
          iconAccessory={bandFilterIconAccessory}
          active={suggestionsFabActive}
          surface="glass"
          directAction
          onPress={() => fabSearch.openSuggestionsFilter()}
        />
      )}
      {showMobileFab && routePathname === AppRoutes.settings && pageQuickLinks.hasPageQuickLinks && (
        <MobileFloatingActionButton
          pageKey="settings"
          ready={pageReady}
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          directAction
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && routePathname === AppRoutes.manual && pageQuickLinks.hasPageQuickLinks && (
        <MobileFloatingActionButton
          pageKey="manual"
          ready={pageReady}
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          directAction
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && (isStatisticsRoute || isPlayerDetailRoute) && (
        <MobileFloatingActionButton
          pageKey={isStatisticsRoute ? 'statistics' : `player:${routePathname}`}
          ready={playerDetailFabReady}
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          sideActions={[...statisticsSideActions, ...playerSelectSideActions]}
          directAction={pageQuickLinks.hasPageQuickLinks}
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && RoutePatterns.history.test(routePathname) && (
        <MobileFloatingActionButton
          pageKey="history"
          ready={pageReady}
          mode="players"
          actionGroups={withPageQuickLinks(
            fabSearch.playerHistoryActionsReady ? [
              { label: t('common.sortPlayerScores'), icon: <IoSwapVerticalSharp size={Size.iconFab} />, onPress: () => fabSearch.openPlayerHistorySort() },
            ] : [],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && RoutePatterns.songDetail.test(routePathname) && (
        <MobileFloatingActionButton
          pageKey="songDetail"
          ready={pageReady}
          mode="players"
          ariaLabel={pageQuickLinks.hasPageQuickLinks ? getFabQuickLinksActionLabel(t) : undefined}
          sideActions={songDetailSideActions}
          directAction={pageQuickLinks.hasPageQuickLinks}
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && routePathname === AppRoutes.shop && (
        <MobileFloatingActionButton
          pageKey="shop"
          ready={pageReady}
          mode="players"
          actionGroups={withPageQuickLinks(
            fabSearch.shopActionsReady && !isNarrowGrid ? [{
              label: fabSearch.shopViewMode === 'grid' ? t('common.listView', 'List View') : t('common.gridView', 'Grid View'),
              icon: fabSearch.shopViewMode === 'grid' ? <IoList size={Size.iconFab} /> : <IoGrid size={Size.iconFab} />,
              onPress: () => fabSearch.shopToggleView(),
            }] : [],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && routePathname === AppRoutes.leaderboards && (
        <MobileFloatingActionButton
          pageKey="leaderboards"
          ready={pageReady}
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          sideActions={leaderboardsSideActions}
          directAction={pageQuickLinks.hasPageQuickLinks}
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && showBandRankingsMetricFab && (
        <MobileFloatingActionButton
          pageKey="bandRankings:metric"
          ready={pageReady}
          mode="players"
          ariaLabel={t('rankings.changeRanking')}
          icon={<IoOptions size={Size.iconFab} />}
          active={fabSearch.leaderboardMetricActive}
          sideActions={leaderboardBandComboSideActions}
          directAction
          onPress={() => fabSearch.openLeaderboardMetric()}
        />
      )}
      {showMobileFab && isBandRankingsRoute && bandRankingsComboOnlyAction && (
        <MobileFloatingActionButton
          pageKey="bandRankings:combo"
          ready={pageReady}
          mode="players"
          ariaLabel={bandRankingsComboOnlyAction.label}
          icon={bandRankingsComboOnlyAction.icon}
          iconAccessory={bandRankingsComboOnlyAction.iconAccessory}
          active={bandRankingsComboOnlyAction.active}
          surface="glass"
          directAction
          onPress={bandRankingsComboOnlyAction.onPress}
        />
      )}
      {showMobileFab && RoutePatterns.leaderboards.test(routePathname) && routePathname !== AppRoutes.leaderboards && !isBandRankingsRoute && (() => {
        const isAllLeaderboardsRoute = routePathname === AppRoutes.fullRankingsRoot;
        const allLeaderboardsParams = isAllLeaderboardsRoute ? new URLSearchParams(location.search) : null;
        const isScopedAllLeaderboardsRoute = !!(allLeaderboardsParams?.has('combo') || allLeaderboardsParams?.has('family'));
        const showAllLeaderboardsMainFab = pageQuickLinks.hasPageQuickLinks && !isScopedAllLeaderboardsRoute;
        const changeInstrumentLabel = t('rankings.changeInstrument');
        const leaderboardInstrumentLabel = serverInstrumentLabel(leaderboardInstrument);
        const leaderboardActions = [
          ...(isAllLeaderboardsRoute ? [] : leaderboardBandComboFabActions),
          ...(isAllLeaderboardsRoute && fabSearch.leaderboardInstrumentReady ? [{ label: changeInstrumentLabel, displayLabel: leaderboardInstrumentLabel, icon: <InstrumentIcon instrument={leaderboardInstrument} size={LEADERBOARD_INSTRUMENT_ACTION_ICON_SIZE} />, onPress: () => fabSearch.openLeaderboardInstrument() }] : []),
          ...(fabSearch.leaderboardMetricReady ? [{ label: t('rankings.changeRanking'), active: fabSearch.leaderboardMetricActive, icon: <IoOptions size={Size.iconFab} />, onPress: () => fabSearch.openLeaderboardMetric() }] : []),
        ];
        if (isAllLeaderboardsRoute) {
          return (
          <MobileFloatingActionButton
            pageKey={`leaderboards:${routePathname}`}
            ready={pageReady}
            mode="players"
            ariaLabel={showAllLeaderboardsMainFab ? getFabQuickLinksActionLabel(t) : undefined}
            sideActions={leaderboardActions.map(action => ({ ...action, iconOnly: action.label !== changeInstrumentLabel }))}
            directAction={showAllLeaderboardsMainFab}
            onPress={() => pageQuickLinks.openPageQuickLinks()}
          />
          );
        }
        return (
        <MobileFloatingActionButton
          pageKey={`leaderboards:${routePathname}`}
          ready={pageReady}
          mode="players"
          actionGroups={withPageQuickLinks(
            leaderboardActions,
          )}
          onPress={() => {}}
        />
        );
      })()}
      {showMobileFab && RoutePatterns.songBandLeaderboard.test(routePathname) && leaderboardBandComboFabActions[0] && (
        <MobileFloatingActionButton
          pageKey="songBandLeaderboard"
          ready={pageReady}
          mode="players"
          ariaLabel={leaderboardBandComboFabActions[0].label}
          icon={leaderboardBandComboFabActions[0].icon}
          iconAccessory={leaderboardBandComboFabActions[0].iconAccessory}
          active={leaderboardBandComboFabActions[0].active}
          surface="glass"
          directAction
          onPress={leaderboardBandComboFabActions[0].onPress}
        />
      )}
      {showMobileFab && RoutePatterns.rivals.test(routePathname) && (
        <MobileFloatingActionButton
          pageKey="rivals"
          ready={pageReady}
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          sideActions={[
            ...(fabSearch.rivalsToggleTabReady ? [{
              label: fabSearch.rivalsActiveTab === 'song' ? t('rivals.tabLeaderboard') : t('rivals.tabSong'),
              icon: fabSearch.rivalsActiveTab === 'song' ? <IoTrophy size={Size.iconFab} /> : <IoMusicalNotes size={Size.iconFab} />,
              onPress: () => fabSearch.rivalsToggleTab(),
            }] : []),
            ...(fabSearch.rivalsFindRivalReady ? [{
              label: t('rivals.findRival'),
              icon: <IoSearch size={Size.iconFab} />,
              onPress: () => fabSearch.rivalsFindRival(),
            }] : []),
          ]}
          directAction
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && RoutePatterns.playerBands.test(routePathname) && fabSearch.bandActionsReady && (
        <MobileFloatingActionButton
          pageKey="playerBands"
          ready={pageReady}
          mode="players"
          ariaLabel={t('common.filterBands')}
          icon={<IoFunnel size={Size.iconFab} />}
          active={playerBandsFilterActive}
          surface="glass"
          directAction
          onPress={() => fabSearch.openBandFilter()}
        />
      )}
      {showMobileFab && RoutePatterns.bands.test(routePathname) && !RoutePatterns.playerBands.test(routePathname) && (
        <MobileFloatingActionButton
          pageKey={`bands:${routePathname}${location.search}`}
          ready={bandDetailFabReady}
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          sideActions={[...bandFilterFabActions, ...bandSelectSideActions]}
          directAction={pageQuickLinks.hasPageQuickLinks}
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && routePathname === AppRoutes.compete && pageQuickLinks.hasPageQuickLinks && (
        <MobileFloatingActionButton
          pageKey="compete"
          ready={pageReady}
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          sideActions={[
            {
              label: t('compete.leaderboards'),
              displayLabel: t('leaderboard.title'),
              icon: <IoTrophy size={Size.iconFab} />,
              onPress: () => navigate(AppRoutes.leaderboards),
            },
            {
              label: t('compete.rivals'),
              icon: <IoPeople size={Size.iconFab} />,
              onPress: () => navigate(AppRoutes.rivals),
            },
          ]}
          directAction
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && RoutePatterns.rivalDetail.test(routePathname) && !RoutePatterns.allRivals.test(routePathname) && (() => {
        const rivalIdMatch = routePathname.match(/^\/rivals\/([^/]+)$/);
        const currentRivalId = rivalIdMatch?.[1];
        const rivalName = new URLSearchParams(location.search).get('name');
        const profileLabel = rivalName ? t('common.viewNameProfile', { name: rivalName }) : t('common.viewProfile');
        return currentRivalId ? (
        <MobileFloatingActionButton
          pageKey={`rivalDetail:${currentRivalId}`}
          ready={pageReady}
          mode="players"
          ariaLabel={pageQuickLinks.hasPageQuickLinks ? getFabQuickLinksActionLabel(t) : undefined}
          sideActions={[{ label: profileLabel, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(getPlayerProfileRoute(currentRivalId, selectedProfile)) }]}
          directAction={pageQuickLinks.hasPageQuickLinks}
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
        ) : null;
      })()}
      {showMobileFab && RoutePatterns.rivalry.test(routePathname) && (() => {
        const rivalryIdMatch = routePathname.match(/^\/rivals\/([^/]+)\/rivalry/);
        const currentRivalId = rivalryIdMatch?.[1];
        const searchParams = new URLSearchParams(location.search);
        const rivalName = searchParams.get('name');
        const hasExplicitMode = searchParams.has('mode');
        const profileLabel = rivalName ? t('common.viewNameProfile', { name: rivalName }) : t('common.viewProfile');
        const showQuickLinksFab = !hasExplicitMode && pageQuickLinks.hasPageQuickLinks;

        return currentRivalId ? (
        <MobileFloatingActionButton
          pageKey={`rivalry:${currentRivalId}:${hasExplicitMode ? 'mode' : 'main'}`}
          ready={pageReady}
          mode="players"
          ariaLabel={showQuickLinksFab ? getFabQuickLinksActionLabel(t) : undefined}
          sideActions={[{ label: profileLabel, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(getPlayerProfileRoute(currentRivalId, selectedProfile)) }]}
          directAction={showQuickLinksFab}
          onPress={showQuickLinksFab ? () => pageQuickLinks.openPageQuickLinks() : () => {}}
        />
        ) : null;
      })()}
      {showMobileFab && knownRoute && routePathname !== AppRoutes.songs && routePathname !== AppRoutes.suggestions && routePathname !== AppRoutes.statistics && routePathname !== AppRoutes.settings && routePathname !== AppRoutes.manual && routePathname !== AppRoutes.shop && routePathname !== AppRoutes.compete && !RoutePatterns.history.test(routePathname) && !RoutePatterns.player.test(routePathname) && !RoutePatterns.songDetail.test(routePathname) && !RoutePatterns.songBandLeaderboard.test(routePathname) && !RoutePatterns.leaderboards.test(routePathname) && !RoutePatterns.rivals.test(routePathname) && !RoutePatterns.rivalDetail.test(routePathname) && !RoutePatterns.rivalry.test(routePathname) && !RoutePatterns.bands.test(routePathname) && (
        <MobileFloatingActionButton
          pageKey={`fallback:${routePathname}`}
          ready={pageReady}
          mode="players"
          actionGroups={withPageQuickLinks(
            pageQuickLinks.hasPageQuickLinks || fabSearch.playerPageSelect ? [
              ...(fabSearch.playerPageSelect
                ? [{ label: t('common.selectPlayerName', { name: fabSearch.playerPageSelect.displayName }), icon: <IoPersonAdd size={Size.iconFab} />, onPress: fabSearch.playerPageSelect.onSelect }]
                : []),
            ] : [],
          )}
          onPress={() => {}}
        />
      )}
      <LazyModalBoundary
        visible={searchOpen}
        title={t('search.title')}
        boundaryName="search-modal"
        onClose={closeSearch}
        load={loadSearchModal}
        isLoaded={isSearchModalLoaded}
        mobileEnterOffset={0}
        initialFocus="panel"
      >
        <LazySearchModal
          visible={searchOpen}
          onClose={closeSearch}
          availableTargets={searchConfig?.availableTargets}
          placeholderKey={searchConfig?.placeholderKey}
        />
      </LazyModalBoundary>
      <LazyModalBoundary
        visible={notificationsOpen}
        title={t('notifications.title')}
        boundaryName="notifications-modal"
        onClose={() => setNotificationsOpen(false)}
        load={loadMobileNotificationsModal}
        isLoaded={isMobileNotificationsModalLoaded}
      >
        <LazyMobileNotificationsModal
          visible={notificationsOpen}
          onClose={() => setNotificationsOpen(false)}
          presentation={isMobile ? 'mobileModal' : 'desktopDrawer'}
          notifications={surfaceNotifications}
          unreadNotificationIds={surfaceUnreadNotificationIds}
          newNotificationIds={surfaceNewNotificationIds}
          notificationsGenerated={notificationFeed.generationStatus === 'generated'}
          onNotificationsSeen={markNotificationsSeen}
          onNotificationOpen={handleNotificationOpen}
        />
      </LazyModalBoundary>
      {selectedProfile && !useNotificationMockData && <NotificationFeedWebSocketBridge profile={selectedProfile} />}
      <LazyModalBoundary
        visible={bandFilterModalOpen && selectedProfile?.type === 'band'}
        title={t('bandFilter.modalTitle')}
        boundaryName="band-filter-modal"
        onClose={() => setBandFilterModalOpen(false)}
        load={loadBandInstrumentFilterModal}
        isLoaded={isBandInstrumentFilterModalLoaded}
      >
        <LazyBandInstrumentFilterModal
          visible={bandFilterModalOpen && selectedProfile?.type === 'band'}
          selectedBand={selectedProfile?.type === 'band' ? selectedProfile : null}
          appliedAssignments={activeBandFilterAssignments}
          onCancel={() => setBandFilterModalOpen(false)}
          onApply={handleApplyBandFilter}
          onReset={handleResetBandFilter}
        />
      </LazyModalBoundary>
      <LazyModalBoundary
        visible={showChangelog}
        title={t('changelog.title')}
        boundaryName="changelog-modal"
        onClose={closeChangelog}
        load={loadChangelogModal}
        isLoaded={isChangelogModalLoaded}
        initialFocus="panel"
      >
        {showChangelog && (
          <LazyChangelogModal
            onDismiss={dismissChangelog}
            onExitComplete={() => setChangelogDismissed(true)}
          />
        )}
      </LazyModalBoundary>
      <LazyModalBoundary
        visible={showDeselectConfirm}
        title={t('common.deselectConfirmTitle')}
        boundaryName="deselect-confirm"
        onClose={() => setShowDeselectConfirm(false)}
        load={loadConfirmAlert}
        isLoaded={isConfirmAlertLoaded}
        initialFocus="panel"
      >
        {showDeselectConfirm && (
          <LazyConfirmAlert
            title={t('common.deselectConfirmTitle')}
            message={t('common.deselectConfirmMessage')}
            onNo={() => setShowDeselectConfirm(false)}
            onYes={confirmDeselect}
            onExitComplete={() => setShowDeselectConfirm(false)}
          />
        )}
      </LazyModalBoundary>
    </div>
    </>
    </FabVisibilityProvider>
    </PlayerDataProvider>
    </BandFilterActionProvider>
  );
}

function ScrollToTop({ layoutKey }: { layoutKey: 'standard' | 'wide' }) {
  const location = useLocation();
  const { key: locationKey, pathname } = location;
  const routePathname = normalizeRoutePathname(pathname);
  const preserveShellScrollKey = (location.state as PreserveShellScrollState | null)?.preserveShellScrollKey;
  const scrollContainerRef = useScrollContainer();
  const previousLayoutKeyRef = useRef(layoutKey);
  const previousPathnameRef = useRef(routePathname);
  const suggestionsRestoreCleanupRef = useRef<(() => void) | null>(null);
  const suggestionsRestoreFrameRef = useRef(0);
  const suggestionsRestoreRequestRef = useRef(0);
  const stopSuggestionsRestoration = useCallback(() => {
    suggestionsRestoreRequestRef.current += 1;
    cancelAnimationFrame(suggestionsRestoreFrameRef.current);
    suggestionsRestoreFrameRef.current = 0;
    suggestionsRestoreCleanupRef.current?.();
    suggestionsRestoreCleanupRef.current = null;
  }, []);
  const startSuggestionsRestoration = useCallback(() => {
    stopSuggestionsRestoration();
    const request = suggestionsRestoreRequestRef.current;
    void loadSuggestionsPage().then(({ beginSuggestionsScrollRestoration }) => {
      if (request !== suggestionsRestoreRequestRef.current) return;
      const scrollElement = scrollContainerRef.current;
      if (scrollElement) {
        suggestionsRestoreCleanupRef.current = beginSuggestionsScrollRestoration(scrollElement);
        return;
      }
      suggestionsRestoreFrameRef.current = requestAnimationFrame(() => {
        suggestionsRestoreFrameRef.current = 0;
        if (request !== suggestionsRestoreRequestRef.current) return;
        const nextScrollElement = scrollContainerRef.current;
        if (nextScrollElement) {
          suggestionsRestoreCleanupRef.current = beginSuggestionsScrollRestoration(nextScrollElement);
        }
      });
    });
  }, [scrollContainerRef, stopSuggestionsRestoration]);
  useEffect(() => {
    if ('scrollRestoration' in history) {
      history.scrollRestoration = 'manual';
    }
  }, []);
  useLayoutEffect(() => {
    const layoutChanged = previousLayoutKeyRef.current !== layoutKey;
    previousLayoutKeyRef.current = layoutKey;
    if (layoutChanged && routePathname === AppRoutes.suggestions) {
      markCurrentSuggestionsScrollRestorable();
    }
  }, [layoutKey, routePathname]);
  useLayoutEffect(() => {
    const previousPathname = previousPathnameRef.current;
    previousPathnameRef.current = routePathname;
    if (previousPathname === AppRoutes.suggestions && routePathname !== AppRoutes.suggestions) {
      markCurrentSuggestionsScrollRestorable();
    }
  }, [routePathname]);
  useEffect(() => {
    if (preserveShellScrollKey && !consumedPreserveShellScrollKeys.has(preserveShellScrollKey)) {
      consumedPreserveShellScrollKeys.add(preserveShellScrollKey);
      return;
    }
    if (routePathname === AppRoutes.suggestions) return;
    // On browser refresh, always scroll to top — page exemptions only apply to in-app navigation
    if (!IS_PAGE_RELOAD) {
      if (routePathname === AppRoutes.songs) return;
      // Song detail pages manage their own scroll restoration
      if (RoutePatterns.songDetail.test(routePathname)) return;
    }
    scrollContainerRef.current?.scrollTo(0, 0);
  }, [routePathname, preserveShellScrollKey, scrollContainerRef]);

  useLayoutEffect(() => {
    if (routePathname === AppRoutes.suggestions && !preserveShellScrollKey) {
      startSuggestionsRestoration();
    } else {
      stopSuggestionsRestoration();
    }
  }, [layoutKey, locationKey, routePathname, preserveShellScrollKey, startSuggestionsRestoration, stopSuggestionsRestoration]);
  return null;
}
