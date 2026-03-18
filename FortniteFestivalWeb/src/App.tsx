import { HashRouter, Routes, Route, Navigate, useLocation, useNavigate, useNavigationType } from 'react-router-dom';
import { IoPerson, IoPersonAdd, IoSearch, IoSwapVerticalSharp, IoFunnel, IoChevronBack, IoFlash } from 'react-icons/io5';
import { useEffect, useState, useMemo, useRef, useCallback, Suspense, lazy } from 'react';
import { useTranslation } from 'react-i18next';
import { FestivalProvider, useFestival } from './contexts/FestivalContext';
import { SettingsProvider } from './contexts/SettingsContext';
import { AnimatedBackground } from './components/shell/AnimatedBackground';
import { useTrackedPlayer, type TrackedPlayer } from './hooks/data/useTrackedPlayer';
import { PlayerDataProvider } from './contexts/PlayerDataContext';
import { useIsMobile, useIsMobileChrome } from './hooks/ui/useIsMobile';
import SongsPage from './pages/songs/SongsPage';
const SongDetailPage = lazy(() => import('./pages/songinfo/SongDetailPage'));
const LeaderboardPage = lazy(() => import('./pages/leaderboard/global/LeaderboardPage'));
const PlayerHistoryPage = lazy(() => import('./pages/leaderboard/player/PlayerHistoryPage'));
const PlayerPage = lazy(() => import('./pages/player/PlayerPage'));
const SuggestionsPage = lazy(() => import('./pages/suggestions/SuggestionsPage'));
const SettingsPage = lazy(() => import('./pages/settings/SettingsPage'));
import { Colors, Size } from '@festival/theme';
import appCss from './App.module.css';
import { resetSongSettingsForDeselect, loadSongSettings, SONG_SETTINGS_CHANGED_EVENT } from './utils/songSettings';
import BackLink from './components/shell/mobile/BackLink';
import { InstrumentIcon } from './components/display/InstrumentIcons';
import { FabSearchProvider, useFabSearch } from './contexts/FabSearchContext';
import { SearchQueryProvider } from './contexts/SearchQueryContext';
import { useSettings } from './contexts/SettingsContext';
import HeaderSearch from './components/shell/desktop/HeaderSearch';
import BottomNav from './components/shell/mobile/BottomNav';
import Sidebar from './components/shell/desktop/Sidebar';
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

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
    <SettingsProvider>
      <FestivalProvider>
        <FabSearchProvider>
          <SearchQueryProvider>
            <HashRouter>
              <AppShell />
            </HashRouter>
          </SearchQueryProvider>
        </FabSearchProvider>
      </FestivalProvider>
    </SettingsProvider>
    <ReactQueryDevtools initialIsOpen={false} />
    </QueryClientProvider>
  );
}

import { useTabNavigation } from './hooks/ui/useTabNavigation';

const CHANGELOG_STORAGE_KEY = 'fst:changelog';

const ANIMATED_BG_ROUTES = new Set(['/', AppRoutes.songs, AppRoutes.suggestions, AppRoutes.statistics, AppRoutes.settings]);
function isAnimatedBgRoute(pathname: string) {
  return ANIMATED_BG_ROUTES.has(pathname) || RoutePatterns.player.test(pathname);
}

function AppShell() {
  const { t } = useTranslation();
  const { player, setPlayer, clearPlayer } = useTrackedPlayer();
  const { state: { songs } } = useFestival();
  const { settings } = useSettings();
  const location = useLocation();
  const isMobile = useIsMobileChrome();
  const isNarrow = useIsMobile();
  const fabSearch = useFabSearch();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [playerModalOpen, setPlayerModalOpen] = useState(false);
  const [findPlayerOpen, setFindPlayerOpen] = useState(false);
  const [changelogOpen, setChangelogOpen] = useState(() => {
    try {
      const stored = localStorage.getItem(CHANGELOG_STORAGE_KEY);
      if (!stored) return true;
      const parsed = JSON.parse(stored);
      return parsed.version !== APP_VERSION || parsed.hash !== changelogHash();
    } catch { return true; }
  });
  // Mark as seen immediately so refresh won't re-show
  useEffect(() => {
    if (changelogOpen) {
      localStorage.setItem(CHANGELOG_STORAGE_KEY, JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));
    }
  }, [changelogOpen]);
  const dismissChangelog = useCallback(() => {
    setChangelogOpen(false);
  }, []);
  const navigate = useNavigate();
  const navType = useNavigationType();

  // Track whether the back button has already appeared in the current detail stack.
  const backShownRef = useRef(false);

  // Clear page caches when score filter settings change so pages restagger
  const filterRef = useRef({ e: settings.filterInvalidScores, l: settings.filterInvalidScoresLeeway });
  useEffect(() => {
    const prev = filterRef.current;
    if (prev.e !== settings.filterInvalidScores || prev.l !== settings.filterInvalidScoresLeeway) {
      filterRef.current = { e: settings.filterInvalidScores, l: settings.filterInvalidScoresLeeway };
      clearSongDetailCache();
      clearLeaderboardCache();
      clearPlayerPageCache();
      // Also invalidate React Query caches so data is refetched with new filter params
      queryClient.invalidateQueries();
    }
  }, [settings.filterInvalidScores, settings.filterInvalidScoresLeeway]);

  // --- Per-tab stack (mobile only) ---
  const { activeTab, handleTabClick } = useTabNavigation();

  const handleSelect = (p: TrackedPlayer) => {
    setPlayer(p);
    // Navigate to statistics unless already on that player's page
    if (location.pathname !== AppRoutes.player(p.accountId)) {
      navigate(AppRoutes.statistics);
    }
  };

  const handleFindPlayerSelect = useCallback((p: TrackedPlayer) => {
    navigate(AppRoutes.player(p.accountId));
  }, [navigate]);

  const handleDeselect = useCallback(() => {
    resetSongSettingsForDeselect();
    clearPlayer();
  }, [clearPlayer]);

  const [songInstrument, setSongInstrument] = useState(() => loadSongSettings().instrument);
  useEffect(() => {
    const sync = () => setSongInstrument(loadSongSettings().instrument);
    window.addEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
    return () => window.removeEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
  }, []);

  const showAnimatedBg = isAnimatedBgRoute(location.pathname);

  // Page title for mobile header
  const NAV_TITLES: Record<string, string> = {
    [AppRoutes.songs]: t('nav.songs'),
    [AppRoutes.suggestions]: t('nav.suggestions'),
    [AppRoutes.statistics]: t('nav.statistics'),
    [AppRoutes.settings]: t('nav.settings'),
  };
  const navTitle = NAV_TITLES[location.pathname] ?? null;

  // Hierarchical back-navigation fallback for detail pages only.
  // Tab routes (songs, suggestions, statistics, settings) never show a back button.
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

  return (
    <PlayerDataProvider accountId={player?.accountId}>
    <div className={appCss.shell}>
      <ScrollToTop />
      {showAnimatedBg && <AnimatedBackground songs={songs} />}

      {!isMobile && backFallback && (IS_IOS || IS_ANDROID || IS_PWA) && <BackLink key={location.pathname} fallback={backFallback} animate={shouldAnimateHeader} />}

        {isMobile ? (
          navTitle ? (
            <div key={location.pathname} className={`sa-top ${appCss.mobileHeader}`} style={shouldAnimateHeader ? { animation: 'fadeIn 300ms ease-out' } : undefined}>
              {backFallback ? (
                <a
                  href="#"
                  onClick={(e) => { e.preventDefault(); navigate(-1); }}
                  className={appCss.navTitleBack}
                >
                  <IoChevronBack size={22} />
                  <span>{navTitle}</span>
                </a>
              ) : (
                <span className={appCss.navTitle}>{navTitle}</span>
              )}
              {location.pathname === AppRoutes.songs && songInstrument && (
                <InstrumentIcon instrument={songInstrument} size={36} style={{ marginLeft: 'auto' }} />
              )}
            </div>
          ) : (
            backFallback ? <BackLink key={location.pathname} fallback={backFallback} animate={shouldAnimateHeader} /> : null
          )
        ) : (
          <nav className={`sa-top ${appCss.nav}`}>
            <button
              className={appCss.hamburger}
              onClick={() => setSidebarOpen((o) => !o)}
              aria-label={t('aria.openNavigation')}
            >
              <span className={appCss.hamburgerLine} />
              <span className={appCss.hamburgerLine} />
              <span className={appCss.hamburgerLine} />
            </button>
            <div className={appCss.spacer} />
            <HeaderSearch />
            <button
              className={appCss.headerProfileBtn}
              onClick={() => player ? navigate(AppRoutes.statistics) : setPlayerModalOpen(true)}
              aria-label={t('aria.profile')}
            >
              <span className={appCss.headerProfileCircleBase} style={{
                backgroundColor: player ? Colors.surfaceSubtle : '#D0D5DD',
                border: player ? `1px solid ${Colors.borderSubtle}` : '1px solid transparent',
                color: player ? Colors.textSecondary : '#4A5568',
              }}>
                <IoPerson size={16} />
              </span>
            </button>
          </nav>
        )}

      <Sidebar
        player={player}
        open={sidebarOpen}
        onClose={() => setSidebarOpen(false)}
        onDeselect={handleDeselect}
        onSelectPlayer={() => { setSidebarOpen(false); setPlayerModalOpen(true); }}
      />

      <div id="main-content" className={appCss.content}>
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
      {changelogOpen && <ChangelogModal onDismiss={dismissChangelog} />}
      {/* v8 ignore stop */}
    </div>
    </PlayerDataProvider>
  );
}

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
    /* v8 ignore next — DOM scroll call */
    document.getElementById('main-content')?.scrollTo(0, 0);
  }, [pathname]);
  return null;
}

