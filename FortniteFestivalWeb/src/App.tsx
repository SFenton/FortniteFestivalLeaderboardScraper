import { HashRouter, Routes, Route, Link, Navigate, useLocation, useNavigate, useNavigationType } from 'react-router-dom';
import { IoPerson, IoPersonAdd, IoSearch, IoSwapVerticalSharp, IoFunnel, IoChevronBack, IoFlash } from 'react-icons/io5';
import { useEffect, useState, useMemo, useRef, useCallback, Suspense, lazy } from 'react';
import { useTranslation } from 'react-i18next';
import { FestivalProvider, useFestival } from './contexts/FestivalContext';
import { SettingsProvider } from './contexts/SettingsContext';
import { AnimatedBackground } from './components/AnimatedBackground';
import { useTrackedPlayer, type TrackedPlayer } from './hooks/useTrackedPlayer';
import { PlayerDataProvider } from './contexts/PlayerDataContext';
import { useIsMobile, useIsMobileChrome } from './hooks/useIsMobile';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from './hooks/useVisualViewport';
import SongsPage from './pages/SongsPage';
const SongDetailPage = lazy(() => import('./pages/SongDetailPage'));
const LeaderboardPage = lazy(() => import('./pages/LeaderboardPage'));
const PlayerHistoryPage = lazy(() => import('./pages/PlayerHistoryPage'));
const PlayerPage = lazy(() => import('./pages/PlayerPage'));
const SuggestionsPage = lazy(() => import('./pages/SuggestionsPage'));
const SettingsPage = lazy(() => import('./pages/SettingsPage'));
import { Colors, Font, Gap, Layout, MaxWidth, Radius, Size, frostedCard } from '@festival/theme';
import appCss from './App.module.css';
import { resetSongSettingsForDeselect, loadSongSettings, SONG_SETTINGS_CHANGED_EVENT } from './components/songSettings';
import BackLink from './components/BackLink';
import { InstrumentIcon } from './components/InstrumentIcons';
import { FabSearchProvider, useFabSearch } from './contexts/FabSearchContext';
import { useSettings } from './contexts/SettingsContext';
import HeaderSearch from './components/shell/HeaderSearch';
import BottomNav from './components/shell/BottomNav';
import Sidebar from './components/shell/Sidebar';
import FloatingActionButton from './components/shell/FloatingActionButton';
import MobilePlayerSearchModal from './components/shell/MobilePlayerSearchModal';
import { clearSongDetailCache } from './pages/SongDetailPage';
import { clearLeaderboardCache } from './pages/LeaderboardPage';
import { clearPlayerPageCache } from './pages/PlayerPage';
import { IS_IOS, IS_ANDROID, IS_PWA } from './utils/platform';
import ChangelogModal from './components/modals/ChangelogModal';
import { APP_VERSION } from './hooks/useVersions';
import { changelogHash } from './changelog';

export default function App() {
  return (
    <SettingsProvider>
      <FestivalProvider>
        <FabSearchProvider>
          <HashRouter>
            <AppShell />
          </HashRouter>
        </FabSearchProvider>
      </FestivalProvider>
    </SettingsProvider>
  );
}

type TabKey = 'songs' | 'suggestions' | 'statistics' | 'settings';
const TAB_ROOTS: Record<TabKey, string> = { songs: '/songs', suggestions: '/suggestions', statistics: '/statistics', settings: '/settings' };

/** Infer which tab owns a route. Detail pages under /songs belong to songs; /player belongs to the active tab. */
function inferTab(pathname: string): TabKey | null {
  if (pathname === '/songs' || pathname.startsWith('/songs/')) return 'songs';
  if (pathname === '/suggestions') return 'suggestions';
  if (pathname === '/statistics') return 'statistics';
  if (pathname === '/settings') return 'settings';
  return null; // /player/:id — ambiguous, owned by the currently active tab
}

const ANIMATED_BG_ROUTES = new Set(['/', '/songs', '/suggestions', '/statistics', '/settings']);
function isAnimatedBgRoute(pathname: string) {
  return ANIMATED_BG_ROUTES.has(pathname) || pathname.startsWith('/player/');
}

function AppShell() {
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
      const stored = localStorage.getItem('fst:changelog');
      if (!stored) return true;
      const parsed = JSON.parse(stored);
      return parsed.version !== APP_VERSION || parsed.hash !== changelogHash();
    } catch { return true; }
  });
  // Mark as seen immediately so refresh won't re-show
  useEffect(() => {
    if (changelogOpen) {
      localStorage.setItem('fst:changelog', JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));
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
    }
  }, [settings.filterInvalidScores, settings.filterInvalidScoresLeeway]);

  // --- Per-tab stack (mobile only) ---
  // Each tab remembers the last route the user was on within it.
  const [activeTab, setActiveTab] = useState<TabKey>(() => inferTab(location.pathname) ?? 'songs');
  const [tabRoutes, setTabRoutes] = useState<Record<TabKey, string>>(() => ({
    songs: '/songs',
    suggestions: '/suggestions',
    statistics: '/statistics',
    settings: '/settings',
  }));

  // Keep tabRoutes in sync: as the user drills deeper, save the current URL to the active tab.
  const prevPathRef = useRef(location.pathname);
  useEffect(() => {
    if (location.pathname === prevPathRef.current) return;
    const previousPath = prevPathRef.current;
    prevPathRef.current = location.pathname;

    // On POP navigation, check if we landed on a route that belongs to a different tab
    if (navType === 'POP') {
      const landedTab = inferTab(location.pathname);
      if (landedTab && landedTab !== activeTab) {
        setActiveTab(landedTab);
        setTabRoutes(prev => ({ ...prev, [landedTab]: location.pathname }));
        return;
      }
    }

    // For PUSH/REPLACE that crosses to a different tab, switch the active tab
    // and save the previous tab's route so switching back resumes where the user was.
    const landedTab = inferTab(location.pathname);
    if (landedTab && landedTab !== activeTab && navType !== 'POP') {
      setActiveTab(landedTab);
      setTabRoutes(prev => ({
        ...prev,
        [activeTab]: previousPath,
        [landedTab]: location.pathname,
      }));
      return;
    }

    // For PUSH/REPLACE within the current tab, update the saved route
    setTabRoutes(prev => ({ ...prev, [activeTab]: location.pathname }));
  }, [location.pathname, navType, activeTab]);

  const handleTabClick = useCallback((tab: TabKey) => {
    if (tab === activeTab) {
      // Re-tap: pop to tab root
      const root = TAB_ROOTS[tab];
      if (location.pathname !== root) {
        navigate(root, { replace: true });
        setTabRoutes(prev => ({ ...prev, [tab]: root }));
      }
      return;
    }
    // Save current location to current tab (except Statistics — always reset to root)
    setTabRoutes(prev => ({
      ...prev,
      [activeTab]: activeTab === 'statistics' ? TAB_ROOTS.statistics : location.pathname,
    }));
    setActiveTab(tab);
    // Statistics always navigates to root
    const target = tab === 'statistics' ? TAB_ROOTS.statistics : tabRoutes[tab];
    navigate(target, { replace: true });
  }, [activeTab, location.pathname, navigate, tabRoutes]);

  const handleSelect = (p: TrackedPlayer) => {
    setPlayer(p);
    // Navigate to statistics unless already on that player's page
    if (location.pathname !== `/player/${p.accountId}`) {
      navigate('/statistics');
    }
  };

  const handleFindPlayerSelect = useCallback((p: TrackedPlayer) => {
    navigate(`/player/${p.accountId}`);
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
    '/songs': 'Songs',
    '/suggestions': 'Suggestions',
    '/statistics': 'Statistics',
    '/settings': 'Settings',
  };
  const navTitle = NAV_TITLES[location.pathname] ?? null;

  // Hierarchical back-navigation fallback for detail pages only.
  // Tab routes (songs, suggestions, statistics, settings) never show a back button.
  const backFallback = useMemo(() => {
    const path = location.pathname;
    const parts = path.split('/').filter(Boolean);
    if (parts[0] === 'songs' && parts.length === 4) return `/songs/${parts[1]}/${parts[2]}`;
    if (parts[0] === 'songs' && parts.length === 3) return `/songs/${parts[1]}`;
    if (parts[0] === 'songs' && parts.length === 2) return '/songs';
    if (parts[0] === 'player' && parts.length === 2) return '/songs';
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
            <div key={location.pathname} className="sa-top" style={{ ...appCss.mobileHeader, ...(shouldAnimateHeader ? { animation: 'fadeIn 300ms ease-out' } : {}) }}>
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
              {location.pathname === '/songs' && songInstrument && (
                <InstrumentIcon instrument={songInstrument} size={36} style={{ marginLeft: 'auto' }} />
              )}
            </div>
          ) : (
            backFallback ? <BackLink key={location.pathname} fallback={backFallback} animate={shouldAnimateHeader} /> : null
          )
        ) : (
          <nav className="sa-top" className={appCss.nav}>
            <button
              className={appCss.hamburger}
              onClick={() => setSidebarOpen((o) => !o)}
              aria-label="Open navigation"
            >
              <span className={appCss.hamburgerLine} />
              <span className={appCss.hamburgerLine} />
              <span className={appCss.hamburgerLine} />
            </button>
            <div className={appCss.spacer} />
            <HeaderSearch />
            <button
              className={appCss.headerProfileBtn}
              onClick={() => player ? navigate('/statistics') : setPlayerModalOpen(true)}
              aria-label="Profile"
            >
              <span style={{
                ...appCss.headerProfileCircleBase,
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
        <Suspense fallback={null}>
        <Routes>
          <Route path="/" element={<Navigate to="/songs" replace />} />
          <Route path="/songs" element={<SongsPage />} />
          <Route path="/songs/:songId" element={<SongDetailPage />} />
          <Route path="/songs/:songId/:instrument" element={<LeaderboardPage />} />
          <Route path="/songs/:songId/:instrument/history" element={<PlayerHistoryPage />} />
          <Route path="/player/:accountId" element={<PlayerPage />} />
          {player ? (
            <Route path="/statistics" element={<PlayerPage accountId={player.accountId} />} />
          ) : (
            <Route path="/statistics" element={<Navigate to="/songs" replace />} />
          )}
          {player ? (
            <Route path="/suggestions" element={<SuggestionsPage accountId={player.accountId} />} />
          ) : (
            <Route path="/suggestions" element={<Navigate to="/songs" replace />} />
          )}
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
        </Suspense>
      </div>

      {isMobile && <BottomNav player={player} activeTab={activeTab} onTabClick={handleTabClick} />}
      {isMobile && location.pathname === '/songs' && (
        <FloatingActionButton
          mode="songs"
          defaultOpen
          placeholder="Search songs or artists..."
          actionGroups={[
            [
              { label: 'Sort Songs', icon: <IoSwapVerticalSharp size={18} />, onPress: () => fabSearch.openSort() },
              ...(player ? [{ label: 'Filter Songs', icon: <IoFunnel size={18} />, onPress: () => fabSearch.openFilter() }] : []),
            ],
            [
              { label: 'Find Player', icon: <IoSearch size={18} />, onPress: () => setFindPlayerOpen(true) },
              player
                ? { label: player.displayName, icon: <IoPerson size={18} />, onPress: () => setPlayerModalOpen(true) }
                : { label: 'Select Player Profile', icon: <IoPerson size={18} />, onPress: () => setPlayerModalOpen(true) },
            ],
          ]}
          onPress={() => {}}
        />
      )}
      {isMobile && location.pathname === '/suggestions' && (
        <FloatingActionButton
          mode="players"
          actionGroups={[
            [
              { label: 'Filter Suggestions', icon: <IoFunnel size={18} />, onPress: () => fabSearch.openSuggestionsFilter() },
            ],
            [
              { label: 'Find Player', icon: <IoSearch size={18} />, onPress: () => setFindPlayerOpen(true) },
              player
                ? { label: player.displayName, icon: <IoPerson size={18} />, onPress: () => setPlayerModalOpen(true) }
                : { label: 'Select Player Profile', icon: <IoPerson size={18} />, onPress: () => setPlayerModalOpen(true) },
            ],
          ]}
          onPress={() => {}}
        />
      )}
      {isMobile && location.pathname.endsWith('/history') && (
        <FloatingActionButton
          mode="players"
          actionGroups={[
            [
              { label: 'Sort Player Scores', icon: <IoSwapVerticalSharp size={18} />, onPress: () => fabSearch.openPlayerHistorySort() },
            ],
            [
              { label: 'Find Player', icon: <IoSearch size={18} />, onPress: () => setFindPlayerOpen(true) },
              player
                ? { label: player.displayName, icon: <IoPerson size={18} />, onPress: () => setPlayerModalOpen(true) }
                : { label: 'Select Player Profile', icon: <IoPerson size={18} />, onPress: () => setPlayerModalOpen(true) },
            ],
          ]}
          onPress={() => {}}
        />
      )}
      {isMobile && /^\/songs\/[^/]+$/.test(location.pathname) && (
        <FloatingActionButton
          mode="players"
          actionGroups={[
            ...(isNarrow ? [[
              { label: 'View Paths', icon: <IoFlash size={18} />, onPress: () => fabSearch.openPaths() },
            ]] : []),
            [
              { label: 'Find Player', icon: <IoSearch size={18} />, onPress: () => setFindPlayerOpen(true) },
              player
                ? { label: player.displayName, icon: <IoPerson size={18} />, onPress: () => setPlayerModalOpen(true) }
                : { label: 'Select Player Profile', icon: <IoPerson size={18} />, onPress: () => setPlayerModalOpen(true) },
            ],
          ]}
          onPress={() => {}}
        />
      )}
      {isMobile && location.pathname !== '/songs' && location.pathname !== '/suggestions' && !location.pathname.endsWith('/history') && !/^\/songs\/[^/]+$/.test(location.pathname) && (
        <FloatingActionButton
          mode="players"
          actionGroups={[
            ...(fabSearch.playerPageSelect ? [[
              { label: `Select ${fabSearch.playerPageSelect.displayName} as Player Profile`, icon: <IoPersonAdd size={18} />, onPress: fabSearch.playerPageSelect.onSelect },
            ]] : []),
            [
              { label: 'Find Player', icon: <IoSearch size={18} />, onPress: () => setFindPlayerOpen(true) },
              player
                ? { label: player.displayName, icon: <IoPerson size={18} />, onPress: () => setPlayerModalOpen(true) }
                : { label: 'Select Player Profile', icon: <IoPerson size={18} />, onPress: () => setPlayerModalOpen(true) },
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
        title="Find Player"
      />
      {changelogOpen && <ChangelogModal onDismiss={dismissChangelog} />}
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
    if (pathname === '/suggestions' || pathname === '/songs') return;
    // Song detail pages manage their own scroll restoration
    if (/^\/songs\/[^/]+$/.test(pathname)) return;
    document.getElementById('main-content')?.scrollTo(0, 0);
  }, [pathname]);
  return null;
}

