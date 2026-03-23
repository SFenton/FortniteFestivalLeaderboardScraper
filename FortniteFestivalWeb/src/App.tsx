import { HashRouter, Routes, Route, Navigate, useLocation, useNavigate, useNavigationType } from 'react-router-dom';
import { IoPerson, IoPersonAdd, IoSearch, IoSwapVerticalSharp, IoFunnel, IoFlash } from 'react-icons/io5';
import { useEffect, useState, useMemo, useRef, useCallback, Suspense, lazy } from 'react';
import { useTranslation } from 'react-i18next';
import { FestivalProvider, useFestival } from './contexts/FestivalContext';
import { SettingsProvider } from './contexts/SettingsContext';
import { AnimatedBackground } from './components/shell/AnimatedBackground';
import { useTrackedPlayer, type TrackedPlayer } from './hooks/data/useTrackedPlayer';
import { PlayerDataProvider } from './contexts/PlayerDataContext';
import { useIsMobile, useIsMobileChrome, useIsWideDesktop } from './hooks/ui/useIsMobile';
import SongsPage from './pages/songs/SongsPage';
/* v8 ignore start -- lazy() wrappers are resolved by the bundler, not callable in unit tests */
const SongDetailPage = lazy(() => import('./pages/songinfo/SongDetailPage'));
const LeaderboardPage = lazy(() => import('./pages/leaderboard/global/LeaderboardPage'));
const PlayerHistoryPage = lazy(() => import('./pages/leaderboard/player/PlayerHistoryPage'));
const PlayerPage = lazy(() => import('./pages/player/PlayerPage'));
const SuggestionsPage = lazy(() => import('./pages/suggestions/SuggestionsPage'));
const SettingsPage = lazy(() => import('./pages/settings/SettingsPage'));
/* v8 ignore stop */
import { Size } from '@festival/theme';
import appCss from './App.module.css';
import { resetSongSettingsForDeselect, loadSongSettings, SONG_SETTINGS_CHANGED_EVENT } from './utils/songSettings';
import BackLink from './components/shell/mobile/BackLink';
import MobileHeader from './components/shell/mobile/MobileHeader';
import { FabSearchProvider, useFabSearch } from './contexts/FabSearchContext';
import { SearchQueryProvider } from './contexts/SearchQueryContext';
import { useSettings } from './contexts/SettingsContext';
import BottomNav from './components/shell/mobile/BottomNav';
import Sidebar from './components/shell/desktop/Sidebar';
import DesktopNav from './components/shell/desktop/DesktopNav';
import PinnedSidebar from './components/shell/desktop/PinnedSidebar';
import FloatingActionButton from './components/shell/fab/FloatingActionButton';
import MobilePlayerSearchModal from './components/shell/mobile/MobilePlayerSearchModal';
import { clearSongDetailCache, clearLeaderboardCache, clearPlayerPageCache } from './api/pageCache';
import { IS_IOS, IS_ANDROID, IS_PWA } from '@festival/ui-utils';
import ChangelogModal from './components/modals/ChangelogModal';
import { APP_VERSION } from './hooks/data/useVersions';
import { changelogHash } from './changelog';
import ErrorBoundary from './components/page/ErrorBoundary';
import SuspenseFallback from './components/common/SuspenseFallback';
import RouteErrorFallback from './components/page/RouteErrorFallback';
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { queryClient } from './api/queryClient';
import { Routes as AppRoutes, RoutePatterns } from './routes';
import { FirstRunProvider, useFirstRunContext } from './contexts/FirstRunContext';

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
    <SettingsProvider>
      <FestivalProvider>
        <FirstRunProvider>
        <FabSearchProvider>
          <SearchQueryProvider>
            <HashRouter>
              <AppShell />
            </HashRouter>
          </SearchQueryProvider>
        </FabSearchProvider>
        </FirstRunProvider>
      </FestivalProvider>
    </SettingsProvider>
    <ReactQueryDevtools initialIsOpen={false} />
    </QueryClientProvider>
  );
}

import { useTabNavigation } from './hooks/ui/useTabNavigation';

const CHANGELOG_STORAGE_KEY = 'fst:changelog';

const ANIMATED_BG_ROUTES = new Set(['/', AppRoutes.songs, AppRoutes.suggestions, AppRoutes.statistics, AppRoutes.settings]);
/* v8 ignore start — route detection helper */
function isAnimatedBgRoute(pathname: string) {
  return ANIMATED_BG_ROUTES.has(pathname) || RoutePatterns.player.test(pathname);
}
/* v8 ignore stop */

function AppShell() {
  const { t } = useTranslation();
  const { player, setPlayer, clearPlayer } = useTrackedPlayer();
  const { state: { songs } } = useFestival();
  const { settings } = useSettings();
  const location = useLocation();
  const isMobile = useIsMobileChrome();
  const isNarrow = useIsMobile();
  const isWideDesktop = useIsWideDesktop();
  const fabSearch = useFabSearch();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [playerModalOpen, setPlayerModalOpen] = useState(false);
  const [findPlayerOpen, setFindPlayerOpen] = useState(false);
  const [hasNewChangelog] = useState(() => {
    try {
      const stored = localStorage.getItem(CHANGELOG_STORAGE_KEY);
      if (!stored) return true;
      const parsed = JSON.parse(stored);
      return parsed.version !== APP_VERSION || parsed.hash !== changelogHash();
    } catch { return true; }
  });
  const [changelogDismissed, setChangelogDismissed] = useState(false);
  const { activeCarouselKey } = useFirstRunContext();
  /* v8 ignore next — activeCarouselKey suppression tested via FirstRunContext tests */
  const showChangelog = hasNewChangelog && !changelogDismissed && !activeCarouselKey;
  /* v8 ignore start — modal dismiss callback */
  const dismissChangelog = useCallback(() => {
    localStorage.setItem(CHANGELOG_STORAGE_KEY, JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));
    setChangelogDismissed(true);
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
      // Also invalidate React Query caches so data is refetched with new filter params
      queryClient.invalidateQueries();
      /* v8 ignore stop */
    }
  }, [settings.filterInvalidScores, settings.filterInvalidScoresLeeway]);

  // --- Per-tab stack (mobile only) ---
  const { activeTab, handleTabClick } = useTabNavigation();

  /* v8 ignore start — deep AppInner: routing/navigation logic embedded in render */
  const handleSelect = (p: TrackedPlayer) => {
    setPlayer(p);
    // Navigate to statistics unless already on that player's page
    if (location.pathname !== AppRoutes.player(p.accountId)) {
      navigate(AppRoutes.statistics);
    }
  };
  /* v8 ignore stop */

  /* v8 ignore start — deep AppInner callback */
  const handleFindPlayerSelect = useCallback((p: TrackedPlayer) => {
    navigate(AppRoutes.player(p.accountId));
  }, [navigate]);
  /* v8 ignore stop */

  /* v8 ignore start — deep AppInner: deselect callback */
  const handleDeselect = useCallback(() => {
    resetSongSettingsForDeselect();
    clearPlayer();
  }, [clearPlayer]);
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
    if (parts[0] === 'player' && parts.length === 2) return AppRoutes.songs;
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

  return (
    <PlayerDataProvider accountId={player?.accountId}>
    <div className={appCss.shell}>
      <ScrollToTop />

      {/* v8 ignore start — sidebar callbacks tested via Sidebar.test / PinnedSidebar.test */}
      {!wideDesktop && (
        <Sidebar
          player={player}
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
          />
        ) : (
          <DesktopNav
            hasPlayer={!!player}
            onOpenSidebar={() => setSidebarOpen((o) => !o)}
            onProfileClick={() => player ? navigate(AppRoutes.statistics) : setPlayerModalOpen(true)}
            isWideDesktop={isWideDesktop}
          />
        )}
      {/* v8 ignore stop */}

      {/* v8 ignore start — wideDesktop layout tested via PinnedSidebar.test */}
      <div className={wideDesktop ? appCss.contentRow : appCss.contentColumn}>
      {wideDesktop && (
        <PinnedSidebar
          player={player}
          onDeselect={handleDeselect}
          onSelectPlayer={() => setPlayerModalOpen(true)}
        />
      )}
      <div id="main-content" className={`${appCss.content}${wideDesktop ? ` ${appCss.contentPinned}` : ''}`}>
      {/* v8 ignore stop */}
        <Suspense fallback={<SuspenseFallback />}>
        <Routes>
          <Route path="/" element={<Navigate to={AppRoutes.songs} replace />} />
          <Route path="/songs" element={<SongsPage />} />
          <Route path="/songs/:songId" element={<ErrorBoundary fallback={<RouteErrorFallback />}><SongDetailPage /></ErrorBoundary>} />
          <Route path="/songs/:songId/:instrument" element={<ErrorBoundary fallback={<RouteErrorFallback />}><LeaderboardPage /></ErrorBoundary>} />
          <Route path="/songs/:songId/:instrument/history" element={<ErrorBoundary fallback={<RouteErrorFallback />}><PlayerHistoryPage /></ErrorBoundary>} />
          <Route path="/player/:accountId" element={<ErrorBoundary fallback={<RouteErrorFallback />}><PlayerPage /></ErrorBoundary>} />
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
          <Route path="/settings" element={<ErrorBoundary fallback={<RouteErrorFallback />}><SettingsPage /></ErrorBoundary>} />
        </Routes>
        </Suspense>
      </div>
      {/* v8 ignore start — wideDesktop spacer */}
      {wideDesktop && <div className={appCss.rightSpacer} />}
      {/* v8 ignore stop */}

      {/* v8 ignore start — mobile FAB configuration tested via MobileFabController + FloatingActionButton tests */}
      {isMobile && <BottomNav player={player} activeTab={activeTab} onTabClick={handleTabClick} />}
      {isMobile && location.pathname === AppRoutes.songs && (
        <FloatingActionButton
          mode="songs"
          defaultOpen
          placeholder={t('songs.searchPlaceholder')}
          actionGroups={[
            [
              { label: t('common.sortSongs'), icon: <IoSwapVerticalSharp size={Size.iconFab} />, onPress: () => fabSearch.openSort() },
              ...(player ? [{ label: t('common.filterSongs'), icon: <IoFunnel size={Size.iconFab} />, onPress: () => fabSearch.openFilter() }] : []),
            ],
            [
              { label: t('common.findPlayer'), icon: <IoSearch size={Size.iconFab} />, onPress: () => setFindPlayerOpen(true) },
              player
                ? { label: player.displayName, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(AppRoutes.statistics) }
                : { label: t('common.selectPlayerProfile'), icon: <IoPerson size={Size.iconFab} />, onPress: () => setPlayerModalOpen(true) },
            ],
          ]}
          onPress={() => {}}
        />
      )}
      {isMobile && location.pathname === AppRoutes.suggestions && (
        <FloatingActionButton
          mode="players"
          actionGroups={[
            [
              { label: t('common.filterSuggestions'), icon: <IoFunnel size={Size.iconFab} />, onPress: () => fabSearch.openSuggestionsFilter() },
            ],
            [
              { label: t('common.findPlayer'), icon: <IoSearch size={Size.iconFab} />, onPress: () => setFindPlayerOpen(true) },
              player
                ? { label: player.displayName, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(AppRoutes.statistics) }
                : { label: t('common.selectPlayerProfile'), icon: <IoPerson size={Size.iconFab} />, onPress: () => setPlayerModalOpen(true) },
            ],
          ]}
          onPress={() => {}}
        />
      )}
      {isMobile && RoutePatterns.history.test(location.pathname) && (
        <FloatingActionButton
          mode="players"
          actionGroups={[
            [
              { label: t('common.sortPlayerScores'), icon: <IoSwapVerticalSharp size={Size.iconFab} />, onPress: () => fabSearch.openPlayerHistorySort() },
            ],
            [
              { label: t('common.findPlayer'), icon: <IoSearch size={Size.iconFab} />, onPress: () => setFindPlayerOpen(true) },
              player
                ? { label: player.displayName, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(AppRoutes.statistics) }
                : { label: t('common.selectPlayerProfile'), icon: <IoPerson size={Size.iconFab} />, onPress: () => setPlayerModalOpen(true) },
            ],
          ]}
          onPress={() => {}}
        />
      )}
      {isMobile && RoutePatterns.songDetail.test(location.pathname) && (
        <FloatingActionButton
          mode="players"
          actionGroups={[
            ...(isNarrow ? [[
              { label: t('common.viewPaths'), icon: <IoFlash size={Size.iconFab} />, onPress: () => fabSearch.openPaths() },
            ]] : []),
            [
              { label: t('common.findPlayer'), icon: <IoSearch size={Size.iconFab} />, onPress: () => setFindPlayerOpen(true) },
              player
                ? { label: player.displayName, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(AppRoutes.statistics) }
                : { label: t('common.selectPlayerProfile'), icon: <IoPerson size={Size.iconFab} />, onPress: () => setPlayerModalOpen(true) },
            ],
          ]}
          onPress={() => {}}
        />
      )}
      {isMobile && location.pathname !== AppRoutes.songs && location.pathname !== AppRoutes.suggestions && !RoutePatterns.history.test(location.pathname) && !RoutePatterns.songDetail.test(location.pathname) && (
        <FloatingActionButton
          mode="players"
          actionGroups={[
            ...(fabSearch.playerPageSelect ? [[
              { label: t('common.selectAsProfile', { name: fabSearch.playerPageSelect.displayName }), icon: <IoPersonAdd size={Size.iconFab} />, onPress: fabSearch.playerPageSelect.onSelect },
            ]] : []),
            [
              { label: t('common.findPlayer'), icon: <IoSearch size={Size.iconFab} />, onPress: () => setFindPlayerOpen(true) },
              player
                ? { label: player.displayName, icon: <IoPerson size={Size.iconFab} />, onPress: () => navigate(AppRoutes.statistics) }
                : { label: t('common.selectPlayerProfile'), icon: <IoPerson size={Size.iconFab} />, onPress: () => setPlayerModalOpen(true) },
            ],
          ]}
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
      <MobilePlayerSearchModal
        visible={findPlayerOpen}
        onClose={() => setFindPlayerOpen(false)}
        onSelect={handleFindPlayerSelect}
        player={null}
        onDeselect={() => {}}
        isMobile={isNarrow}
        title={t('common.findPlayer')}
      />
      {showChangelog && <ChangelogModal onDismiss={dismissChangelog} />}
      {/* v8 ignore stop */}
      </div>
    </div>
    </PlayerDataProvider>
  );
}

/* v8 ignore start — scroll restoration utility */
function ScrollToTop() {
  const { pathname } = useLocation();
  useEffect(() => {
    if ('scrollRestoration' in history) {
      history.scrollRestoration = 'manual';
    }
  }, []);
  useEffect(() => {
    if (pathname === AppRoutes.suggestions || pathname === AppRoutes.songs) return;
    // Song detail pages manage their own scroll restoration
    if (RoutePatterns.songDetail.test(pathname)) return;
    document.getElementById('main-content')?.scrollTo(0, 0);
  }, [pathname]);
  return null;
}
/* v8 ignore stop */

