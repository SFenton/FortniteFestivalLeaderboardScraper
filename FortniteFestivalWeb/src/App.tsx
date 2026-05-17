import { HashRouter, Routes, Route, Navigate, useLocation, useNavigate, useNavigationType } from 'react-router-dom';
import { IoCompass, IoPerson, IoPersonAdd, IoSwapVerticalSharp, IoFunnel, IoFlash, IoGrid, IoList, IoOptions, IoMusicalNotes, IoTrophy, IoBagHandle, IoPeople, IoSearch } from 'react-icons/io5';
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
      {player || selectedBand ? (
        <Route path="/suggestions" element={<ErrorBoundary fallback={<RouteErrorFallback />}><SuggestionsPage accountId={player?.accountId} selectedBand={selectedBand} /></ErrorBoundary>} />
      ) : (
        <Route path="/suggestions" element={<Navigate to={AppRoutes.songs} replace />} />
      )}
      <Route path="/shop" element={<ErrorBoundary fallback={<RouteErrorFallback />}><ShopPage /></ErrorBoundary>} />
      <Route path="/manual" element={<ManualRouteElement />} />
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
      <Route path="/settings/licenses" element={<ErrorBoundary fallback={<RouteErrorFallback />}><LicensesPage /></ErrorBoundary>} />
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
import { BandFilterActionProvider, type BandFilterActionContextValue } from './contexts/BandFilterActionContext';
import { SearchQueryProvider } from './contexts/SearchQueryContext';
import { useSettings, visibleInstruments, visiblePathInstruments } from './contexts/SettingsContext';
import { useShopState } from './hooks/data/useShopState';
import { useProximityGlow } from './hooks/ui/useProximityGlow';
import BottomNav from './components/shell/mobile/BottomNav';
import Sidebar from './components/shell/desktop/Sidebar';
import DesktopNav from './components/shell/desktop/DesktopNav';
import PinnedSidebar from './components/shell/desktop/PinnedSidebar';
import FloatingActionButton, { type ActionItem } from './components/shell/fab/FloatingActionButton';
import { InstrumentIcon } from './components/display/InstrumentIcons';
import SearchModal from './components/search/SearchModal';
import MobileNotificationsModal, { filterSurfaceNotifications, type MobileNotification } from './components/notifications/MobileNotificationsModal';
import { getNotificationDestination } from './components/notifications/notificationDestination';
import { useNotificationFreshnessState } from './components/notifications/notificationFreshnessState';
import { notificationFeedKeyForProfile, useNotificationSeenState } from './components/notifications/notificationSeenState';
import { NotificationFeedWebSocketBridge, useProfileNotificationsFeed } from './components/notifications/useProfileNotificationsFeed';
import type { SearchTarget } from './types/search';
import { clearSongDetailCache, clearLeaderboardCache, clearPlayerPageCache } from './api/pageCache';
import { IS_IOS, IS_ANDROID, IS_PWA, IS_PAGE_RELOAD } from '@festival/ui-utils';
import ChangelogModal from './components/modals/ChangelogModal';
import ConfirmAlert from './components/modals/ConfirmAlert';
import BandInstrumentFilterModal, { type BandInstrumentFilterApplyPayload, type BandInstrumentFilterAssignment } from './pages/band/modals/BandInstrumentFilterModal';
import { DEFAULT_INSTRUMENT, SERVER_INSTRUMENT_KEYS, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import type { AppliedBandComboFilter } from './types/bandFilter';
import { APP_VERSION } from './hooks/data/useVersions';
import { changelogHash } from './changelog';
import ErrorBoundary from './components/page/ErrorBoundary';
import SuspenseFallback from './components/common/SuspenseFallback';
import RouteErrorFallback from './components/page/RouteErrorFallback';
import type { PreserveShellScrollState } from './utils/quietNavigation';
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
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { queryClient } from './api/queryClient';
import { Routes as AppRoutes, RoutePatterns } from './routes';
import { FirstRunProvider, useFirstRunContext } from './contexts/FirstRunContext';
import { ScrollContainerProvider, useShellRefs, useScrollContainer, HEADER_PORTAL_HEIGHT_VAR } from './contexts/ScrollContainerContext';
import { FeatureFlagsProvider, useFeatureFlagsState } from './contexts/FeatureFlagsContext';
import { useTapDiagnostics } from './diagnostics/useTapDiagnostics';
import anim from './styles/animations.module.css';

const consumedPreserveShellScrollKeys = new Set<string>();
const showReactQueryDevtools = import.meta.env.DEV && import.meta.env.MODE !== 'e2e';
const NOTIFICATIONS_VALIDATION_TOKEN = 'notifications-open';
const EMPTY_NOTIFICATIONS_VALIDATION_TOKEN = 'notifications-empty';
const MOCK_NOTIFICATION_SOURCE_VERSION = 'mock-source-2026-05-09';
const PROFILE_SEARCH_TARGETS: readonly SearchTarget[] = ['players', 'bands'];
const FAB_COMBO_INSTRUMENT_ICON_SIZE = Size.iconSm;
const FAB_COMBO_INSTRUMENTS_STYLE = {
  display: 'flex',
  alignItems: 'center',
  gap: 4,
  lineHeight: 0,
} as const;
const FAB_COMBO_INSTRUMENT_ICON_STYLE = {
  display: 'block',
  flexShrink: 0,
} as const;

function ManualRouteElement() {
  const { flags, resolved } = useFeatureFlagsState();
  if (!resolved) return <SuspenseFallback />;
  if (!flags.appManual) return <Navigate to={AppRoutes.songs} replace />;
  return <ErrorBoundary fallback={<RouteErrorFallback />}><ManualPage /></ErrorBoundary>;
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
    {showReactQueryDevtools && <ReactQueryDevtools initialIsOpen={false} />}
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
  return selectedProfile?.type === 'band' && !pathname.startsWith(AppRoutes.settings) && pathname !== AppRoutes.manual;
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
  const hasSideActions = (props.sideActions?.length ?? 0) > 0;
  if (!props.defaultOpen && !hasActions && !hasSideActions && !props.directAction) return null;
  return <FloatingActionButton {...props} actionGroups={actionGroups} />;
}

function ComboInstrumentFabAccessory({ instruments }: { instruments: readonly ServerInstrumentKey[] }) {
  if (instruments.length === 0) return null;

  return (
    <span data-testid="fab-band-filter-instruments" aria-hidden="true" style={FAB_COMBO_INSTRUMENTS_STYLE}>
      {instruments.map((instrument, index) => (
        <InstrumentIcon
          key={`${instrument}:${index}`}
          instrument={instrument}
          size={FAB_COMBO_INSTRUMENT_ICON_SIZE}
          style={FAB_COMBO_INSTRUMENT_ICON_STYLE}
        />
      ))}
    </span>
  );
}

function resolveLeaderboardInstrument(search: string): ServerInstrumentKey {
  const value = new URLSearchParams(search).get('instrument');
  return value && SERVER_INSTRUMENT_KEYS.includes(value as ServerInstrumentKey)
    ? value as ServerInstrumentKey
    : DEFAULT_INSTRUMENT;
}

function getSongDetailId(pathname: string): string | undefined {
  const match = pathname.match(/^\/songs\/([^/]+)$/);
  return match?.[1] ? decodeURIComponent(match[1]) : undefined;
}

const ANIMATED_BG_ROUTES = new Set(['/', AppRoutes.songs, AppRoutes.suggestions, AppRoutes.statistics, AppRoutes.manual, AppRoutes.settings, AppRoutes.settingsLicenses, AppRoutes.shop, AppRoutes.compete, AppRoutes.leaderboards]);
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
  const { profile: selectedProfile, player, clearPlayer } = useTrackedPlayer();
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
  const notificationFeedReadyForHeader = useNotificationMockData || notificationFeed.status !== 'loading';
  const notificationFeedKey = notificationFeed.feedKey;
  const { unreadNotificationIds, markNotificationsSeen } = useNotificationSeenState(notificationFeedKey, notificationIds);
  const { newNotificationIds } = useNotificationFreshnessState(notificationFeedKey, notificationIds, notificationFeed.sourceVersion);
  const notificationInstrumentFilter = useMemo(() => {
    if (notificationRequestProfile?.type !== 'player') return null;
    return new Set(visibleInstruments(settings));
  }, [notificationRequestProfile?.type, settings]);
  const surfaceNotifications = useMemo(
    () => filterSurfaceNotifications(notificationFeed.notifications, notificationInstrumentFilter),
    [notificationFeed.notifications, notificationInstrumentFilter],
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

  // Proximity glow for frosted cards — document-level for full coverage
  useProximityGlow(!settings.disableLightTrails);
  usePlayerBandsPrefetch(player?.accountId);

  const location = useLocation();
  const leaderboardInstrument = useMemo(() => resolveLeaderboardInstrument(location.search), [location.search]);
  const isMobile = useIsMobileChrome();
  const isNarrow = useIsMobile();
  const isNarrowGrid = useMediaQuery(QUERY_NARROW_GRID);
  const isWideDesktop = useIsWideDesktop();
  const hasVisiblePathInstruments = visiblePathInstruments(settings).length > 0;
  const fabSearch = useFabSearch();
  const { isShopVisible, isShopHighlighted, isLeavingTomorrow, getShopUrl } = useShopState();
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
  /* v8 ignore next — activeCarouselKey suppression tested via FirstRunContext tests */
  const showChangelog = hasNewChangelog && !changelogDismissed && !activeCarouselKey;
  /* v8 ignore start — modal dismiss callback */
  const dismissChangelog = useCallback(() => {
    localStorage.setItem(CHANGELOG_STORAGE_KEY, JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));
  }, []);
  /* v8 ignore stop */
  const navigate = useNavigate();
  const navType = useNavigationType();

  const openSearch = useCallback((config?: SearchModalConfig) => {
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
  const notificationRequestMatchesSelection = selectedProfile != null && selectedNotificationFeedKey === requestedNotificationFeedKey;
  const canOpenNotifications = selectedProfile != null && notificationRequestMatchesSelection && notificationFeedReadyForHeader && !notificationHeaderBusy;
  const handleOpenNotifications = useCallback(() => setNotificationsOpen(true), []);

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

  /* v8 ignore start — deep AppInner: deselect callback */
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
    [AppRoutes.manual]: t('nav.manual'),
    [AppRoutes.settings]: t('nav.settings'),
    [AppRoutes.compete]: t('compete.title'),
    [AppRoutes.rivals]: fabSearch.rivalsActiveTab === 'song' ? t('rivals.tabSong') : t('rivals.tabLeaderboard'),
    [AppRoutes.leaderboards]: t('rankings.title'),
    [AppRoutes.shop]: t('nav.shop'),
  };
  const navTitle = location.pathname === AppRoutes.statistics
    ? (player?.displayName ?? (selectedProfile?.type === 'band' ? selectedProfile.displayName : t('nav.statistics')))
    : (NAV_TITLES[location.pathname] ?? null);

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
  const showBandFilterAction = shouldShowBandFilterAction(selectedProfile, location.pathname);
  const bandFilterLabel = getBandFilterActionLabel(selectedBandFilterInstruments, emptyBandFilterLabel);
  const bandFilterActive = selectedBandFilterInstruments.length > 0;
  const bandFilterIconAccessory = bandFilterActive
    ? <ComboInstrumentFabAccessory instruments={selectedBandFilterInstruments} />
    : undefined;
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
    appliedAssignments: activeBandFilterAssignments,
    onPress: handleBandFilterPress,
    onApplyFilter: handleApplyBandFilter,
    onResetFilter: handleResetBandFilter,
  }), [activeBandFilter, activeBandFilterAssignments, bandFilterLabel, handleApplyBandFilter, handleBandFilterPress, handleResetBandFilter, isMobile, selectedBandFilterInstruments, showBandFilterAction]);
  const leaderboardsSideActions: ActionItem[] = isMobile && location.pathname === AppRoutes.leaderboards
    ? [
      ...(showBandFilterAction ? [{
      label: bandFilterLabel,
      active: bandFilterActive,
      iconOnly: true,
      icon: <IoFunnel size={Size.iconFab} />,
      iconAccessory: bandFilterIconAccessory,
      onPress: handleBandFilterPress,
    }] : []),
      ...(fabSearch.leaderboardMetricReady ? [{ label: t('rankings.changeRanking'), iconOnly: true, icon: <IoOptions size={Size.iconFab} />, onPress: () => fabSearch.openLeaderboardMetric() }] : []),
    ]
    : [];
  const bandFilterFabActions: ActionItem[] = isMobile && showBandFilterAction && location.pathname !== AppRoutes.leaderboards
    ? [{ label: bandFilterLabel, active: bandFilterActive, icon: <IoFunnel size={Size.iconFab} />, iconAccessory: bandFilterIconAccessory, onPress: handleBandFilterPress }]
    : [];
  const statisticsSideActions: ActionItem[] = isMobile && location.pathname === AppRoutes.statistics && !player && selectedProfile?.type === 'band'
    ? bandFilterFabActions.map(action => ({ ...action, iconOnly: true }))
    : [];
  const playerSelectSideActions: ActionItem[] = isMobile && RoutePatterns.player.test(location.pathname) && fabSearch.playerPageSelect
    ? [{ label: t('common.selectPlayerName', { name: fabSearch.playerPageSelect.displayName }), icon: <IoPersonAdd size={Size.iconFab} />, onPress: fabSearch.playerPageSelect.onSelect }]
    : [];
  const bandSelectSideActions: ActionItem[] = isMobile && RoutePatterns.bands.test(location.pathname) && !RoutePatterns.playerBands.test(location.pathname) && fabSearch.bandPageSelect
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
  const songsQuickLinksAvailable = pageQuickLinks.hasPageQuickLinks && pageQuickLinks.pageQuickLinks != null;
  const songDetailId = getSongDetailId(location.pathname);
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
      className: isShopHighlighted(songDetailId) ? (isLeavingTomorrow(songDetailId) ? anim.shopCircleBreatheRed : anim.shopCircleBreathe) : undefined,
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
  const songsDockActions: ActionItem[] = [
    { label: t('common.sortSongs'), displayLabel: t('common.sort', 'Sort'), active: fabSearch.songsSortActive, icon: <IoSwapVerticalSharp size={Size.iconFab} />, onPress: () => fabSearch.openSort() },
    ...(player || selectedProfile?.type === 'band' ? [{ label: t('common.filterSongs'), displayLabel: t('common.filter', 'Filter'), active: fabSearch.songsFilterActive || bandFilterActive, icon: <IoFunnel size={Size.iconFab} />, iconAccessory: bandFilterIconAccessory, onPress: () => fabSearch.openFilter() }] : []),
  ];
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
          onSelectPlayer={() => { setSidebarOpen(false); openProfileSearch(); }}
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
            onOpenSearch={() => openSearch()}
            onOpenNotifications={canOpenNotifications ? handleOpenNotifications : undefined}
            hasNotifications={hasNotifications}
            notificationCount={surfaceUnreadCount}
            notificationVisualState={notificationHeaderVisualState}
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
          onSelectPlayer={openProfileSearch}
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
          dockActions={fabSearch.songsActionsReady ? songsDockActions : []}
          ariaLabel={songsQuickLinksAvailable ? getFabQuickLinksActionLabel(t) : undefined}
          directAction={songsQuickLinksAvailable}
          onPress={songsQuickLinksAvailable ? () => pageQuickLinks.openPageQuickLinks() : () => {}}
        />
      )}
      {showMobileFab && location.pathname === AppRoutes.suggestions && fabSearch.suggestionsActionsReady && (
        <MobileFloatingActionButton
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
      {showMobileFab && location.pathname === AppRoutes.settings && pageQuickLinks.hasPageQuickLinks && (
        <MobileFloatingActionButton
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          directAction
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && location.pathname === AppRoutes.manual && pageQuickLinks.hasPageQuickLinks && (
        <MobileFloatingActionButton
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          directAction
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && (location.pathname === AppRoutes.statistics || RoutePatterns.player.test(location.pathname)) && (pageQuickLinks.hasPageQuickLinks || statisticsSideActions.length > 0 || playerSelectSideActions.length > 0) && (
        <MobileFloatingActionButton
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          sideActions={[...statisticsSideActions, ...playerSelectSideActions]}
          directAction={pageQuickLinks.hasPageQuickLinks}
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && RoutePatterns.history.test(location.pathname) && (fabSearch.playerHistoryActionsReady || pageQuickLinks.hasPageQuickLinks || bandFilterFabActions.length > 0) && (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            fabSearch.playerHistoryActionsReady ? [
              { label: t('common.sortPlayerScores'), icon: <IoSwapVerticalSharp size={Size.iconFab} />, onPress: () => fabSearch.openPlayerHistorySort() },
            ] : [],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && RoutePatterns.songDetail.test(location.pathname) && (pageQuickLinks.hasPageQuickLinks || songDetailSideActions.length > 0) && (
        <MobileFloatingActionButton
          mode="players"
          ariaLabel={pageQuickLinks.hasPageQuickLinks ? getFabQuickLinksActionLabel(t) : undefined}
          sideActions={songDetailSideActions}
          directAction={pageQuickLinks.hasPageQuickLinks}
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && location.pathname === AppRoutes.shop && (
        <MobileFloatingActionButton
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
      {showMobileFab && location.pathname === AppRoutes.leaderboards && (pageQuickLinks.hasPageQuickLinks || leaderboardsSideActions.length > 0) && (
        <MobileFloatingActionButton
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          sideActions={leaderboardsSideActions}
          directAction={pageQuickLinks.hasPageQuickLinks}
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && RoutePatterns.leaderboards.test(location.pathname) && location.pathname !== AppRoutes.leaderboards && (() => {
        const leaderboardActions = [
          ...(location.pathname === '/leaderboards/all' && fabSearch.leaderboardInstrumentReady ? [{ label: t('rankings.changeInstrument'), icon: <InstrumentIcon instrument={leaderboardInstrument} size={Size.iconFab} />, onPress: () => fabSearch.openLeaderboardInstrument() }] : []),
          ...(fabSearch.leaderboardMetricReady ? [{ label: t('rankings.changeRanking'), icon: <IoOptions size={Size.iconFab} />, onPress: () => fabSearch.openLeaderboardMetric() }] : []),
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
          directAction={pageQuickLinks.hasPageQuickLinks}
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && RoutePatterns.playerBands.test(location.pathname) && (
        <MobileFloatingActionButton
          mode="players"
          actionGroups={withPageQuickLinks(
            fabSearch.bandActionsReady ? [{ label: t('common.filterBands'), icon: <IoFunnel size={Size.iconFab} />, onPress: () => fabSearch.openBandFilter() }] : [],
          )}
          onPress={() => {}}
        />
      )}
      {showMobileFab && RoutePatterns.bands.test(location.pathname) && !RoutePatterns.playerBands.test(location.pathname) && (pageQuickLinks.hasPageQuickLinks || bandFilterFabActions.length > 0 || bandSelectSideActions.length > 0) && (
        <MobileFloatingActionButton
          mode="players"
          ariaLabel={getFabQuickLinksActionLabel(t)}
          sideActions={[...bandFilterFabActions, ...bandSelectSideActions]}
          directAction={pageQuickLinks.hasPageQuickLinks}
          onPress={() => pageQuickLinks.openPageQuickLinks()}
        />
      )}
      {showMobileFab && location.pathname === AppRoutes.compete && pageQuickLinks.hasPageQuickLinks && (
        <MobileFloatingActionButton
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
      {showMobileFab && location.pathname !== AppRoutes.songs && location.pathname !== AppRoutes.suggestions && location.pathname !== AppRoutes.statistics && location.pathname !== AppRoutes.settings && location.pathname !== AppRoutes.manual && location.pathname !== AppRoutes.shop && location.pathname !== AppRoutes.compete && !RoutePatterns.history.test(location.pathname) && !RoutePatterns.player.test(location.pathname) && !RoutePatterns.songDetail.test(location.pathname) && !RoutePatterns.leaderboards.test(location.pathname) && !RoutePatterns.rivals.test(location.pathname) && !RoutePatterns.rivalDetail.test(location.pathname) && !RoutePatterns.rivalry.test(location.pathname) && !RoutePatterns.bands.test(location.pathname) && (
        <MobileFloatingActionButton
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
      <SearchModal
        visible={searchOpen}
        onClose={closeSearch}
        availableTargets={searchConfig?.availableTargets}
        placeholderKey={searchConfig?.placeholderKey}
      />
      <MobileNotificationsModal
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

