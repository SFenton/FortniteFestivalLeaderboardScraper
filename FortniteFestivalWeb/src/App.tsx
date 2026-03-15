import { HashRouter, Routes, Route, NavLink, Link, Navigate, useLocation, useNavigate, useNavigationType } from 'react-router-dom';
import { IoMusicalNotes, IoSparkles, IoStatsChart, IoPerson, IoPersonAdd, IoSettings, IoSearch, IoSwapVerticalSharp, IoFunnel, IoChevronBack, IoClose, IoFlash } from 'react-icons/io5';
import { useEffect, useLayoutEffect, useState, useMemo, useRef, useCallback, Fragment } from 'react';
import { useTranslation } from 'react-i18next';
import { FestivalProvider, useFestival } from './contexts/FestivalContext';
import { SettingsProvider } from './contexts/SettingsContext';
import { AnimatedBackground } from './components/AnimatedBackground';
import { useTrackedPlayer, type TrackedPlayer } from './hooks/useTrackedPlayer';
import { PlayerDataProvider } from './contexts/PlayerDataContext';
import { api } from './api/client';
import type { AccountSearchResult } from './models';
import { useIsMobile, useIsMobileChrome } from './hooks/useIsMobile';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from './hooks/useVisualViewport';
import SongsPage from './pages/SongsPage';
import SongDetailPage from './pages/SongDetailPage';
import LeaderboardPage from './pages/LeaderboardPage';
import PlayerHistoryPage from './pages/PlayerHistoryPage';
import PlayerPage from './pages/PlayerPage';
import SuggestionsPage from './pages/SuggestionsPage';
import SettingsPage from './pages/SettingsPage';
import { Colors, Font, Gap, Layout, MaxWidth, Radius, Size, frostedCard } from './theme';
import { resetSongSettingsForDeselect, loadSongSettings, SONG_SETTINGS_CHANGED_EVENT } from './components/songSettings';
import BackLink from './components/BackLink';
import { InstrumentIcon } from './components/InstrumentIcons';
import { FabSearchProvider, useFabSearch } from './contexts/FabSearchContext';
import { useSettings } from './contexts/SettingsContext';
import { clearSongDetailCache } from './pages/SongDetailPage';
import { clearLeaderboardCache } from './pages/LeaderboardPage';
import { clearPlayerPageCache } from './pages/PlayerPage';
import { IS_IOS, IS_ANDROID, IS_PWA } from './utils/platform';
import ChangelogModal from './components/ChangelogModal';
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
    <div style={styles.shell}>
      <ScrollToTop />
      {showAnimatedBg && <AnimatedBackground songs={songs} />}

      {!isMobile && backFallback && (IS_IOS || IS_ANDROID || IS_PWA) && <BackLink key={location.pathname} fallback={backFallback} animate={shouldAnimateHeader} />}

        {isMobile ? (
          navTitle ? (
            <div key={location.pathname} className="sa-top" style={{ ...styles.mobileHeader, ...(shouldAnimateHeader ? { animation: 'fadeIn 300ms ease-out' } : {}) }}>
              {backFallback ? (
                <a
                  href="#"
                  onClick={(e) => { e.preventDefault(); navigate(-1); }}
                  style={styles.navTitleBack}
                >
                  <IoChevronBack size={22} />
                  <span>{navTitle}</span>
                </a>
              ) : (
                <span style={styles.navTitle}>{navTitle}</span>
              )}
              {location.pathname === '/songs' && songInstrument && (
                <InstrumentIcon instrument={songInstrument} size={36} style={{ marginLeft: 'auto' }} />
              )}
            </div>
          ) : (
            backFallback ? <BackLink key={location.pathname} fallback={backFallback} animate={shouldAnimateHeader} /> : null
          )
        ) : (
          <nav className="sa-top" style={styles.nav}>
            <button
              style={styles.hamburger}
              onClick={() => setSidebarOpen((o) => !o)}
              aria-label="Open navigation"
            >
              <span style={styles.hamburgerLine} />
              <span style={styles.hamburgerLine} />
              <span style={styles.hamburgerLine} />
            </button>
            <div style={styles.spacer} />
            <HeaderSearch />
            <button
              style={styles.headerProfileBtn}
              onClick={() => player ? navigate('/statistics') : setPlayerModalOpen(true)}
              aria-label="Profile"
            >
              <span style={{
                ...styles.headerProfileCircleBase,
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

      <div id="main-content" style={styles.content}>
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

function HeaderSearch() {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<AccountSearchResult[]>([]);
  const [isOpen, setIsOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const navigate = useNavigate();

  const search = useCallback(async (q: string) => {
    if (q.length < 2) { setResults([]); setIsOpen(false); return; }
    try {
      const res = await api.searchAccounts(q, 10);
      setResults(res.results);
      setIsOpen(res.results.length > 0);
      setActiveIndex(-1);
    } catch { setResults([]); setIsOpen(false); }
  }, []);

  const handleChange = (value: string) => {
    setQuery(value);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => { void search(value.trim()); }, 300);
  };

  const handleSelect = (r: AccountSearchResult) => {
    navigate(`/player/${r.accountId}`);
    setQuery('');
    setResults([]);
    setIsOpen(false);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (!isOpen || results.length === 0) return;
    if (e.key === 'ArrowDown') { e.preventDefault(); setActiveIndex(p => (p < results.length - 1 ? p + 1 : 0)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActiveIndex(p => (p > 0 ? p - 1 : results.length - 1)); }
    else if (e.key === 'Enter' && activeIndex >= 0) { e.preventDefault(); const r = results[activeIndex]; if (r) handleSelect(r); }
    else if (e.key === 'Escape') { setIsOpen(false); }
  };

  useEffect(() => {
    const handleClick = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) setIsOpen(false);
    };
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);

  return (
    <div ref={containerRef} style={styles.headerSearchContainer}>
      <div style={styles.headerSearchInputWrap} onClick={() => inputRef.current?.focus()}>
        <IoSearch size={16} style={{ color: Colors.textTertiary, flexShrink: 0 }} />
        <input
          ref={inputRef}
          style={styles.headerSearchInput}
          placeholder="Search player…"
          value={query}
          onChange={e => handleChange(e.target.value)}
          onKeyDown={handleKeyDown}
          onFocus={() => { if (results.length > 0) setIsOpen(true); }}
        />
      </div>
      {isOpen && (
        <div style={styles.headerSearchDropdown}>
          {results.map((r, i) => (
            <button
              key={r.accountId}
              style={{
                ...styles.headerSearchResult,
                ...(i === activeIndex ? { backgroundColor: Colors.surfaceSubtle } : {}),
              }}
              onClick={() => handleSelect(r)}
              onMouseEnter={() => setActiveIndex(i)}
            >
              {r.displayName}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

const SIDEBAR_DURATION = 250;

function Sidebar({
  player,
  open,
  onClose,
  onDeselect,
  onSelectPlayer,
}: {
  player: TrackedPlayer | null;
  open: boolean;
  onClose: () => void;
  onDeselect: () => void;
  onSelectPlayer: () => void;
}) {
  const { t } = useTranslation();
  const sidebarRef = useRef<HTMLDivElement>(null);
  const location = useLocation();
  const [mounted, setMounted] = useState(false);
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    if (open) {
      setMounted(true);
    } else {
      setVisible(false);
    }
  }, [open]);

  useLayoutEffect(() => {
    if (mounted && open) {
      sidebarRef.current?.getBoundingClientRect();
      const id = requestAnimationFrame(() => setVisible(true));
      return () => cancelAnimationFrame(id);
    }
  }, [mounted, open]);

  const handleTransitionEnd = useCallback(() => {
    if (!visible) setMounted(false);
  }, [visible]);

  useEffect(() => {
    if (!mounted) return;
    function handleClick(e: MouseEvent) {
      if (sidebarRef.current && !sidebarRef.current.contains(e.target as Node)) {
        onClose();
      }
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [mounted, onClose]);

  if (!mounted) return null;

  return (
    <>
      <div
        style={{
          ...styles.overlay,
          opacity: visible ? 1 : 0,
          transition: `opacity ${SIDEBAR_DURATION}ms ease`,
        }}
        onClick={onClose}
      />
      <div
        ref={sidebarRef}
        style={{
          ...styles.sidebar,
          transform: visible ? 'translateX(0)' : `translateX(-100%)`,
          transition: `transform ${SIDEBAR_DURATION}ms ease`,
        }}
        onTransitionEnd={handleTransitionEnd}
      >
        <div style={styles.sidebarHeader}>
          <span style={styles.brand}>Festival Score Tracker</span>
        </div>
        <nav style={styles.sidebarNav}>
          <NavLink
            to="/songs"
            onClick={onClose}
            style={({ isActive }) => ({
              ...styles.sidebarLink,
              ...(isActive ? styles.sidebarLinkActive : {}),
            })}
          >
            {t('nav.songs')}
          </NavLink>
          {player && (
            <NavLink
              to="/suggestions"
              onClick={onClose}
              style={({ isActive }) => ({
                ...styles.sidebarLink,
                ...(isActive ? styles.sidebarLinkActive : {}),
              })}
            >
              {t('nav.suggestions')}
            </NavLink>
          )}
          {player && (
            <NavLink
              to="/statistics"
              onClick={onClose}
              style={({ isActive }) => ({
                ...styles.sidebarLink,
                ...(isActive ? styles.sidebarLinkActive : {}),
              })}
            >
              {t('nav.statistics')}
            </NavLink>
          )}
        </nav>
        <div style={styles.sidebarFooter}>
          {player ? (
            <div style={styles.sidebarPlayerRow}>
              <Link
                to="/statistics"
                onClick={onClose}
                style={{
                  ...styles.sidebarLink,
                  flex: 1,
                  display: 'flex',
                  alignItems: 'center',
                }}
              >
                <span style={styles.profileCircle}>
                  <IoPerson size={14} />
                </span>
                {player.displayName}
              </Link>
              <button
                style={{ ...styles.deselectBtn, marginRight: Gap.section }}
                onClick={() => { onDeselect(); }}
              >
                Deselect
              </button>
            </div>
          ) : (
            <button
              style={{ ...styles.sidebarLink, display: 'flex', alignItems: 'center', background: 'none', border: 'none', cursor: 'pointer' }}
              onClick={onSelectPlayer}
            >
              <span style={styles.profileCircleEmpty}>
                <IoPerson size={14} />
              </span>
              Select Player
            </button>
          )}
          <NavLink
            to="/settings"
            onClick={onClose}
            style={({ isActive }) => ({
              ...styles.sidebarLink,
              ...(isActive ? styles.sidebarLinkActive : {}),
            })}
          >
            Settings
          </NavLink>
        </div>
      </div>
    </>
  );
}

const MODAL_TRANSITION_MS = 250;

function MobilePlayerSearchModal({
  visible,
  onClose,
  onSelect,
  player,
  onDeselect,
  isMobile,
  title = 'Select Player Profile',
}: {
  visible: boolean;
  onClose: () => void;
  onSelect: (p: TrackedPlayer) => void;
  player: TrackedPlayer | null;
  onDeselect: () => void;
  isMobile: boolean;
  title?: string;
}) {
  const [mounted, setMounted] = useState(false);
  const [animIn, setAnimIn] = useState(false);
  const [contentReady, setContentReady] = useState(false);
  const [dismissing, setDismissing] = useState(false);
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<AccountSearchResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [showSpinner, setShowSpinner] = useState(false);
  const [spinnerOpacity, setSpinnerOpacity] = useState(0);
  const [resultsReady, setResultsReady] = useState(false);
  const [resultSeq, setResultSeq] = useState(0);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const vvHeight = useVisualViewportHeight();
  const vvOffsetTop = useVisualViewportOffsetTop();

  useEffect(() => {
    if (visible) {
      setMounted(true);
    } else {
      setAnimIn(false);
    }
  }, [visible]);

  useLayoutEffect(() => {
    if (mounted && visible) {
      const id = requestAnimationFrame(() => setAnimIn(true));
      setTimeout(() => inputRef.current?.focus(), MODAL_TRANSITION_MS);
      return () => cancelAnimationFrame(id);
    }
  }, [mounted, visible]);

  const handleTransitionEnd = useCallback(() => {
    if (animIn) {
      setContentReady(true);
    } else {
      setMounted(false);
      setContentReady(false);
      setDismissing(false);
      setQuery('');
      setResults([]);
      setShowSpinner(false);
      setSpinnerOpacity(0);
      setResultsReady(false);
    }
  }, [animIn]);

  useEffect(() => {
    if (!mounted) return;
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [mounted, onClose]);

  // When loading starts, show spinner immediately and fade it in
  useEffect(() => {
    if (loading) {
      setResultsReady(false);
      setShowSpinner(true);
      // RAF to ensure the 0 opacity is painted before transitioning to 1
      requestAnimationFrame(() => requestAnimationFrame(() => setSpinnerOpacity(1)));
    } else if (showSpinner) {
      // Loading finished — fade spinner out, then reveal results
      setSpinnerOpacity(0);
    }
  }, [loading]); // eslint-disable-line react-hooks/exhaustive-deps

  const handleSpinnerTransitionEnd = useCallback(() => {
    if (spinnerOpacity === 0 && !loading) {
      setShowSpinner(false);
      setResultsReady(true);
    }
  }, [spinnerOpacity, loading]);

  const search = useCallback(async (q: string) => {
    if (q.length < 2) {
      setResults([]);
      return;
    }
    setLoading(true);
    setResults([]);
    try {
      const res = await api.searchAccounts(q, 10);
      setResults(res.results);
      setResultSeq(s => s + 1);
    } catch {
      setResults([]);
    } finally {
      setLoading(false);
    }
  }, []);

  const handleChange = (value: string) => {
    setQuery(value);
    if (value.trim().length < 2) {
      if (debounceRef.current) clearTimeout(debounceRef.current);
      setResults([]);
      setLoading(false);
      setShowSpinner(false);
      setSpinnerOpacity(0);
      setResultsReady(true);
      return;
    }
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      void search(value.trim());
    }, 300);
  };

  const handleSelect = (r: AccountSearchResult) => {
    onSelect({ accountId: r.accountId, displayName: r.displayName });
    onClose();
  };

  const handleDeselect = useCallback(() => {
    if (dismissing) return;
    setDismissing(true);
    // Total reverse stagger: last element at 0ms + 400ms anim = items finish by ~850ms
    setTimeout(() => {
      onDeselect();
      setDismissing(false);
      onClose();
    }, 850);
  }, [dismissing, onDeselect, onClose]);

  const stagger = (delayMs: number): React.CSSProperties =>
    dismissing
      ? { animation: `fadeOutDown 400ms ease-in ${delayMs}ms forwards` }
      : contentReady
        ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards` }
        : { opacity: 0 };

  if (!mounted) return null;

  return (
    <>
      <div
        style={{
          position: 'fixed',
          inset: 0,
          backgroundColor: Colors.overlayModal,
          zIndex: 1000,
          opacity: animIn ? 1 : 0,
          transition: `opacity ${MODAL_TRANSITION_MS}ms ease`,
        }}
        onClick={onClose}
      />
      <div
        role="dialog"
        aria-modal="true"
        aria-label="Select Player"
        style={{
          position: 'fixed',
          ...(isMobile
            ? { left: 0, right: 0, top: vvOffsetTop + vvHeight * 0.2, height: vvHeight * 0.8, borderTopLeftRadius: Radius.lg, borderTopRightRadius: Radius.lg }
            : { top: '50%', left: '50%', width: 420, height: 600, maxHeight: '90vh', borderRadius: Radius.lg, transform: animIn ? 'translate(-50%, -50%)' : 'translate(-50%, -40%)', opacity: animIn ? 1 : 0 }
          ),
          zIndex: 1001,
          display: 'flex',
          flexDirection: 'column' as const,
          backgroundColor: Colors.surfaceFrosted,
          backdropFilter: 'blur(18px)',
          WebkitBackdropFilter: 'blur(18px)',
          color: Colors.textPrimary,
          ...(isMobile
            ? { transform: animIn ? 'translateY(0)' : 'translateY(100%)' }
            : {}
          ),
          transition: isMobile
            ? `transform ${MODAL_TRANSITION_MS}ms ease`
            : `opacity ${MODAL_TRANSITION_MS}ms ease, transform ${MODAL_TRANSITION_MS}ms ease`,
        }}
        onTransitionEnd={handleTransitionEnd}
      >
        <div style={styles.modalHeader}>
          <h2 style={styles.modalTitle}>{title}</h2>
          <button style={styles.modalCloseBtn} onClick={onClose} aria-label="Close"><IoClose size={18} /></button>
        </div>
        <div style={styles.modalBody}>
          {player && (
            <div style={styles.modalPlayerCard}>
              <span style={{ ...styles.profileCircleLg, ...stagger(dismissing ? 450 : 0) }}>
                <IoPerson size={32} />
              </span>
              <span style={{ ...styles.modalPlayerName, ...stagger(dismissing ? 300 : 150) }}>{player.displayName}</span>
              <span style={{ ...styles.modalDeselectHint, ...stagger(dismissing ? 150 : 300) }}>Deselecting will hide suggestions, statistics, and per-song scores from the song list.</span>
              <button
                style={{ ...styles.deselectBtn, ...stagger(dismissing ? 0 : 450), ...(dismissing ? { pointerEvents: 'none' as const } : {}) }}
                onClick={handleDeselect}
              >
                Deselect Player
              </button>
            </div>
          )}
          {!player && (
            <>
              <div style={{ ...styles.modalSearchPill, ...stagger(0) }} onClick={e => { const input = e.currentTarget.querySelector('input'); input?.focus(); }}>
                <IoSearch size={16} style={{ color: Colors.textTertiary, flexShrink: 0 }} />
                <input
                  ref={inputRef}
                  style={styles.modalSearchInput}
                  placeholder="Search player…"
                  value={query}
                  onChange={(e) => handleChange(e.target.value)}
                  onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
                  enterKeyHint="done"
                />
              </div>
              <div style={{ ...styles.modalResults, ...stagger(150) }}>
                {showSpinner && (
                  <div
                    style={{ ...styles.modalSpinnerWrap, opacity: spinnerOpacity, transition: 'opacity 250ms ease' }}
                    onTransitionEnd={handleSpinnerTransitionEnd}
                  >
                    <div style={styles.modalArcSpinner} />
                  </div>
                )}
                {!showSpinner && !loading && query.length < 2 && (
                  <div style={styles.modalHintCenter}>Enter a username to search for.</div>
                )}
                {!showSpinner && !loading && query.length >= 2 && results.length === 0 && (
                  <div style={styles.modalHintCenter}>No matching username found.</div>
                )}
                {!showSpinner && resultsReady && results.map((r, i) => (
                  <button
                    key={`${resultSeq}-${r.accountId}`}
                    style={{
                      ...styles.modalResultBtn,
                      opacity: 0,
                      animation: `fadeInUp 300ms ease-out ${i * 50}ms forwards`,
                    }}
                    onClick={() => handleSelect(r)}
                  >
                    {r.displayName}
                  </button>
                ))}
              </div>
            </>
          )}
        </div>
      </div>
    </>
  );
}

function BottomNav({ player, activeTab, onTabClick }: { player: TrackedPlayer | null; activeTab: TabKey; onTabClick: (tab: TabKey) => void }) {
  const { t } = useTranslation();
  const tabs: { key: TabKey; label: string; icon: React.ReactNode }[] = [
    { key: 'songs', label: t('nav.songs'), icon: <IoMusicalNotes size={20} /> },
    ...(player ? [{ key: 'suggestions' as TabKey, label: t('nav.suggestions'), icon: <IoSparkles size={20} /> }] : []),
    ...(player ? [{ key: 'statistics' as TabKey, label: t('nav.statistics'), icon: <IoStatsChart size={20} /> }] : []),
    { key: 'settings', label: t('nav.settings'), icon: <IoSettings size={20} /> },
  ];

  return (
    <nav style={{ ...styles.bottomNav, ...(IS_PWA ? { paddingBottom: Gap.section } : {}) }}>
      {tabs.map((tab) => (
        <button
          key={tab.key}
          onClick={() => onTabClick(tab.key)}
          style={{
            ...styles.bottomTab,
            ...(activeTab === tab.key ? styles.bottomTabActive : {}),
          }}
        >
          <span style={styles.bottomTabIcon}>{tab.icon}</span>
          {tab.label}
        </button>
      ))}
    </nav>
  );
}

function FloatingActionButton({
  mode,
  defaultOpen,
  placeholder,
  icon,
  actionGroups,
  onPress: _onPress,
}: {
  mode: 'players' | 'songs';
  defaultOpen?: boolean;
  placeholder?: string;
  icon?: React.ReactNode;
  actionGroups?: { label: string; icon: React.ReactNode; onPress: () => void }[][];
  onPress: () => void;
}) {
  const searchVisible = !!defaultOpen;
  const [actionsOpen, setActionsOpen] = useState(false);
  const [popupMounted, setPopupMounted] = useState(false);
  const [popupVisible, setPopupVisible] = useState(false);

  const openActions = useCallback(() => {
    setActionsOpen(true);
    setPopupMounted(true);
    requestAnimationFrame(() => requestAnimationFrame(() => setPopupVisible(true)));
  }, []);

  const closeActions = useCallback(() => {
    setPopupVisible(false);
    setActionsOpen(false);
    setTimeout(() => { setPopupMounted(false); }, 300);
  }, []);
  const fabSearch = useFabSearch();
  const [query, setQuery] = useState(mode === 'songs' ? fabSearch.query : '');
  const [results, setResults] = useState<AccountSearchResult[]>([]);
  const [activeIndex, setActiveIndex] = useState(-1);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const searchContainerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const navigate = useNavigate();

  const searchPlayers = useCallback(async (q: string) => {
    if (q.length < 2) { setResults([]); return; }
    try {
      const res = await api.searchAccounts(q, 10);
      setResults(res.results);
      setActiveIndex(-1);
    } catch { setResults([]); }
  }, []);

  const handleChange = (value: string) => {
    setQuery(value);
    if (mode === 'songs') {
      fabSearch.setQuery(value);
    } else {
      if (debounceRef.current) clearTimeout(debounceRef.current);
      debounceRef.current = setTimeout(() => { void searchPlayers(value.trim()); }, 300);
    }
  };

  const handleSelectPlayer = (r: AccountSearchResult) => {
    navigate(`/player/${r.accountId}`);
    setQuery('');
    setResults([]);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      if (mode === 'players' && activeIndex >= 0) {
        e.preventDefault();
        const r = results[activeIndex];
        if (r) handleSelectPlayer(r);
        return;
      }
      // Dismiss virtual keyboard
      (e.target as HTMLInputElement).blur();
      return;
    }
    if (mode !== 'players' || results.length === 0) return;
    if (e.key === 'ArrowDown') { e.preventDefault(); setActiveIndex(p => (p < results.length - 1 ? p + 1 : 0)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActiveIndex(p => (p > 0 ? p - 1 : results.length - 1)); }
    else if (e.key === 'Escape') { setResults([]); }
  };

  useEffect(() => {
    if (mode !== 'players' || results.length === 0) return;
    const handleClick = (e: MouseEvent) => {
      if (searchContainerRef.current && !searchContainerRef.current.contains(e.target as Node)) {
        setResults([]);
      }
    };
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [mode, results]);

  useEffect(() => {
    if (!actionsOpen) return;
    const handler = (e: MouseEvent) => {
      if (searchContainerRef.current && !searchContainerRef.current.contains(e.target as Node)) {
        e.stopPropagation();
        closeActions();
      }
    };
    document.addEventListener('click', handler, true);
    return () => document.removeEventListener('click', handler, true);
  }, [actionsOpen, closeActions]);

  useEffect(() => {
    if (searchVisible) {
      setTimeout(() => inputRef.current?.focus(), 50);
    }
  }, [searchVisible]);

  return (
    <div ref={searchContainerRef}>
      {searchVisible && (
        <div style={{ ...styles.fabSearchBarOuter, ...(IS_PWA ? { bottom: 80 + Gap.section - Gap.md } : {}) }}>
          <div className="fab-search-bar" style={styles.fabSearchBar}>
            <div style={styles.fabSearchInputWrap} onClick={() => inputRef.current?.focus()}>
              <IoSearch size={16} style={{ color: Colors.textTertiary, flexShrink: 0 }} />
              <input
                ref={inputRef}
                style={styles.fabSearchInput}
                placeholder={placeholder ?? 'Search player\u2026'}
                value={query}
                onChange={e => handleChange(e.target.value)}
                onKeyDown={handleKeyDown}
                enterKeyHint="done"
              />
            </div>
            {mode === 'players' && results.length > 0 && (
              <div style={styles.fabSearchResults}>
                {results.map((r, i) => (
                  <button
                    key={r.accountId}
                    style={{
                      ...styles.fabSearchResultBtn,
                      ...(i === activeIndex ? { backgroundColor: Colors.surfaceSubtle } : {}),
                    }}
                    onClick={() => handleSelectPlayer(r)}
                  >
                    {r.displayName}
                  </button>
                ))}
              </div>
            )}
          </div>
        </div>
      )}
      <div style={{ ...styles.fabContainer, ...(IS_PWA ? { bottom: 80 + Gap.section - Gap.md } : {}) }}>
        <button
          style={styles.fab}
          onClick={() => actionsOpen ? closeActions() : openActions()}
          aria-label="Actions"
        >
          {icon ?? <span style={styles.fabHamburger}><span style={styles.fabHamburgerLine} /><span style={styles.fabHamburgerLine} /><span style={styles.fabHamburgerLine} /></span>}
        </button>
        {popupMounted && (
          <div
            style={{
              position: 'absolute',
              bottom: 64,
              right: 0,
              zIndex: 1002,
              pointerEvents: 'auto' as const,
              ...frostedCard,
              backgroundColor: Colors.backgroundCard,
              borderRadius: Radius.sm,
              padding: `${Gap.sm}px 0`,
              minWidth: 200,
              whiteSpace: 'nowrap' as const,
              boxShadow: '0 4px 20px rgba(0,0,0,0.4)',
              transformOrigin: 'bottom right',
              transform: popupVisible ? 'scale(1)' : 'scale(0)',
              opacity: popupVisible ? 1 : 0,
              transition: popupVisible
                ? 'transform 450ms cubic-bezier(0.34, 1.56, 0.64, 1), opacity 300ms ease'
                : 'transform 300ms ease, opacity 300ms ease',
            }}
          >
            {(actionGroups ?? []).map((group, gi) => (
              <Fragment key={gi}>
                {gi > 0 && <div style={styles.fabPopupDivider} />}
                {group.map((action) => (
                  <button
                    key={action.label}
                    style={styles.fabPopupItem}
                    onClick={() => { closeActions(); action.onPress(); }}
                  >
                    <span style={styles.fabPopupItemIcon}>{action.icon}</span>
                    {action.label}
                  </button>
                ))}
              </Fragment>
            ))}
          </div>
        )}
      </div>
    </div>
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

const SIDEBAR_WIDTH = 280;

const styles: Record<string, React.CSSProperties> = {
  shell: {
    display: 'flex',
    flexDirection: 'column' as const,
    height: '100dvh',
    overflow: 'hidden',
  },
  nav: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px ${Gap.md}px`,
    backgroundColor: 'transparent',
    flexShrink: 0,
    zIndex: 100,
    position: 'relative' as const,
    touchAction: 'none' as const,
  },
  content: {
    flex: 1,
    overflowY: 'auto' as const,
    overscrollBehavior: 'contain' as const,
    position: 'relative' as const,
  },
  hamburger: {
    display: 'flex',
    flexDirection: 'column' as const,
    justifyContent: 'center',
    gap: 5,
    width: 36,
    height: 36,
    padding: 6,
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    borderRadius: Radius.xs,
  },
  hamburgerLine: {
    display: 'block',
    height: 2,
    backgroundColor: Colors.textSecondary,
    borderRadius: 1,
  },
  navTitle: {
    fontSize: Font.title,
    fontWeight: 700,
    color: Colors.textPrimary,
    whiteSpace: 'nowrap' as const,
  },
  navTitleBack: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: Gap.sm,
    fontSize: Font.title,
    fontWeight: 700,
    color: Colors.textPrimary,
    textDecoration: 'none',
    whiteSpace: 'nowrap' as const,
    marginLeft: -6,
  },
  mobileHeader: {
    display: 'flex',
    alignItems: 'center',
    padding: `${Layout.paddingTop + Gap.md}px ${Layout.paddingHorizontal}px ${Gap.md}px`,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    width: '100%',
    boxSizing: 'border-box' as const,
    flexShrink: 0,
    zIndex: 100,
    position: 'relative' as const,
    touchAction: 'none' as const,
  },
  spacer: {
    flex: 1,
  },
  headerSearchContainer: {
    position: 'relative' as const,
    flex: 1,
    maxWidth: 320,
  },
  headerSearchInputWrap: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.sm,
    height: 48,
    padding: `0 ${Gap.xl}px`,
    borderRadius: Radius.full,
    boxSizing: 'border-box' as const,
    cursor: 'text',
    ...frostedCard,
  },
  headerSearchInput: {
    flex: 1,
    background: 'none',
    border: 'none',
    outline: 'none',
    color: Colors.textPrimary,
    fontSize: Font.md,
  },
  headerSearchDropdown: {
    position: 'absolute' as const,
    top: '100%',
    left: 0,
    right: 0,
    marginTop: Gap.sm,
    ...frostedCard,
    backgroundColor: Colors.backgroundCard,
    borderRadius: Radius.sm,
    zIndex: 300,
    maxHeight: 400,
    overflowY: 'auto' as const,
  },
  headerSearchResult: {
    display: 'block',
    width: '100%',
    padding: `${Gap.xl}px ${Gap.section}px`,
    background: 'none',
    border: 'none',
    color: Colors.textSecondary,
    fontSize: Font.md,
    cursor: 'pointer',
    textAlign: 'left' as const,
  },
  brand: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.accentPurple,
  },

  // Sidebar flyout
  overlay: {
    position: 'fixed' as const,
    inset: 0,
    backgroundColor: Colors.overlayDark,
    zIndex: 200,
  },
  sidebar: {
    position: 'fixed' as const,
    top: 0,
    left: 0,
    bottom: 0,
    width: SIDEBAR_WIDTH,
    ...frostedCard,
    backgroundColor: Colors.backgroundCard,
    borderRight: `1px solid ${Colors.glassBorder}`,
    zIndex: 201,
    display: 'flex',
    flexDirection: 'column' as const,
    overflow: 'hidden',
  },
  sidebarHeader: {
    padding: `${Gap.section}px ${Gap.section}px ${Gap.xl}px`,
    borderBottom: `1px solid ${Colors.borderSubtle}`,
  },
  sidebarNav: {
    display: 'flex',
    flexDirection: 'column' as const,
    padding: `${Gap.md}px 0`,
    flex: 1,
  },
  sidebarFooter: {
    borderTop: `1px solid ${Colors.borderSubtle}`,
    padding: `${Gap.md}px 0`,
  },
  sidebarLink: {
    display: 'block',
    padding: `${Gap.xl}px ${Gap.section}px`,
    color: Colors.textSecondary,
    textDecoration: 'none',
    fontSize: Font.md,
    transition: 'background-color 0.15s, color 0.15s',
  },
  sidebarLinkActive: {
    color: Colors.textPrimary,
    backgroundColor: Colors.surfaceSubtle,
    borderLeft: `3px solid ${Colors.accentPurple}`,
  },
  sidebarPlayerRow: {
    display: 'flex',
    alignItems: 'center',
  },
  profileCircle: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 28,
    height: 28,
    borderRadius: '50%',
    backgroundColor: Colors.surfaceSubtle,
    border: `1px solid ${Colors.borderSubtle}`,
    flexShrink: 0,
    marginRight: Gap.md,
  },
  profileCircleEmpty: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 28,
    height: 28,
    borderRadius: '50%',
    backgroundColor: '#D0D5DD',
    border: 'none',
    color: '#4A5568',
    flexShrink: 0,
    marginRight: Gap.md,
  },
  headerProfileBtn: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    padding: 0,
  },
  headerProfileCircleBase: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 48,
    height: 48,
    borderRadius: '50%',
    transition: 'background-color 300ms ease, border-color 300ms ease, color 300ms ease',
  },
  deselectBtn: {
    background: Colors.dangerBg,
    border: `1px solid ${Colors.statusRed}`,
    borderRadius: Radius.xs,
    color: Colors.textPrimary,
    fontSize: Font.sm,
    fontWeight: 600,
    cursor: 'pointer',
    padding: `${Gap.sm}px ${Gap.xl}px`,
    whiteSpace: 'nowrap' as const,
  },

  // Bottom nav (mobile)
  bottomNav: {
    display: 'flex',
    justifyContent: 'space-around',
    alignItems: 'center',
    backgroundColor: Colors.glassNav,
    backdropFilter: 'blur(12px) saturate(1.2)',
    WebkitBackdropFilter: 'blur(12px) saturate(1.2)',
    borderTop: `1px solid ${Colors.glassBorder}`,
    flexShrink: 0,
    zIndex: 100,
    position: 'relative' as const,
    padding: `${Gap.sm}px 0 ${Gap.md}px`,
  },
  fabContainer: {
    position: 'fixed' as const,
    bottom: 80,
    right: Layout.paddingHorizontal,
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'flex-end',
    gap: Gap.md,
    zIndex: 150,
    pointerEvents: 'none' as const,
  },
  fab: {
    width: 56,
    height: 56,
    borderRadius: '50%',
    ...frostedCard,
    backgroundColor: 'rgb(124, 58, 237)',
    border: `1px solid rgba(124, 58, 237, 0.35)`,
    color: Colors.textPrimary,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    boxShadow: '0 4px 16px rgba(0,0,0,0.4)',
    flexShrink: 0,
    pointerEvents: 'auto' as const,
  },
  fabHamburger: {
    display: 'flex',
    flexDirection: 'column' as const,
    justifyContent: 'center',
    gap: 5,
  },
  fabHamburgerLine: {
    display: 'block',
    width: 20,
    height: 2,
    backgroundColor: Colors.textPrimary,
    borderRadius: 1,
  },
  fabSearchBarOuter: {
    position: 'fixed' as const,
    bottom: 80,
    left: 0,
    right: 0,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `0 ${Layout.paddingHorizontal}px`,
    boxSizing: 'border-box' as const,
    zIndex: 150,
    pointerEvents: 'none' as const,
  },
  fabSearchBar: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.sm,
    position: 'relative' as const,
    pointerEvents: 'auto' as const,
  },
  fabSearchInputWrap: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.sm,
    width: '100%',
    height: 56,
    padding: `0 ${Gap.section}px`,
    borderRadius: Radius.full,
    ...frostedCard,
    boxSizing: 'border-box' as const,
    boxShadow: '0 4px 16px rgba(0,0,0,0.3)',
    cursor: 'text',
  },
  fabSearchInput: {
    flex: 1,
    background: 'none',
    border: 'none',
    outline: 'none',
    color: Colors.textPrimary,
    fontSize: Font.md,
  },
  fabSearchResults: {
    position: 'absolute' as const,
    bottom: '100%',
    right: 0,
    left: 0,
    marginBottom: Gap.sm,
    ...frostedCard,
    borderRadius: Radius.sm,
    maxHeight: 360,
    overflowY: 'auto' as const,
    boxShadow: '0 -4px 16px rgba(0,0,0,0.3)',
  },
  fabSearchResultBtn: {
    display: 'block',
    width: '100%',
    padding: `${Gap.xl}px ${Gap.section}px`,
    background: 'none',
    border: 'none',
    color: Colors.textSecondary,
    fontSize: Font.md,
    cursor: 'pointer',
    textAlign: 'left' as const,
  },
  fabPopupItem: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    width: '100%',
    padding: `${Gap.xl}px ${Gap.section}px`,
    background: 'none',
    border: 'none',
    color: Colors.textSecondary,
    fontSize: Font.md,
    cursor: 'pointer',
    textAlign: 'left' as const,
  },
  fabPopupItemIcon: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 20,
    flexShrink: 0,
    color: Colors.textTertiary,
  },
  fabPopupDivider: {
    height: 1,
    backgroundColor: Colors.glassBorder,
    margin: `${Gap.sm}px 0`,
  },
  bottomTab: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    gap: 2,
    background: 'none',
    border: 'none',
    textDecoration: 'none',
    color: Colors.textTertiary,
    fontSize: Font.xs,
    fontFamily: 'inherit',
    padding: `${Gap.sm}px ${Gap.xl}px`,
    borderRadius: Radius.xs,
    transition: 'color 0.15s',
    cursor: 'pointer',
    minWidth: 64,
  },
  bottomTabActive: {
    color: Colors.accentPurple,
  },
  bottomTabIcon: {
    fontSize: 20,
    lineHeight: '24px',
  },

  // Mobile player search modal
  modalHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: `${Gap.xl}px 16px ${Gap.xl}px ${Gap.section}px`,
    flexShrink: 0,
  },
  modalTitle: {
    fontSize: Font.xl,
    fontWeight: 700,
    margin: 0,
  },
  modalCloseBtn: {
    width: 32,
    height: 32,
    borderRadius: '50%',
    background: Colors.surfaceElevated,
    border: `1px solid ${Colors.borderPrimary}`,
    color: Colors.textSecondary,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    flexShrink: 0,
  },
  modalBody: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column' as const,
    padding: `${Gap.sm}px ${Gap.section}px ${Gap.section}px`,
    gap: Gap.md,
    overflow: 'hidden',
  },
  modalPlayerCard: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    gap: Gap.xl,
    padding: `${Gap.section * 2}px ${Gap.section}px`,
    flex: 1,
    justifyContent: 'center',
  },
  profileCircleLg: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 64,
    height: 64,
    borderRadius: '50%',
    backgroundColor: Colors.surfaceSubtle,
    border: `1px solid ${Colors.borderSubtle}`,
    flexShrink: 0,
  },
  modalPlayerName: {
    fontSize: Font.xl,
    fontWeight: 700,
    color: Colors.textPrimary,
  },
  modalDeselectHint: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
    textAlign: 'center' as const,
    lineHeight: '1.5',
  },
  modalSearchPill: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.sm,
    height: 48,
    padding: `0 ${Gap.xl}px`,
    boxSizing: 'border-box' as const,
    borderRadius: Radius.full,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.backgroundCard,
    cursor: 'text',
    flexShrink: 0,
  },
  modalSearchInput: {
    flex: 1,
    background: 'none',
    border: 'none',
    outline: 'none',
    color: Colors.textPrimary,
    fontSize: Font.md,
  },
  modalResults: {
    flex: 1,
    overflowY: 'auto' as const,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
  },
  modalHint: {
    padding: `${Gap.md}px ${Gap.xl}px`,
    color: Colors.textTertiary,
    fontSize: Font.sm,
  },
  modalSpinnerWrap: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flex: 1,
  },
  modalHintCenter: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flex: 1,
    color: Colors.textTertiary,
    fontSize: Font.lg,
    textAlign: 'center' as const,
  },
  modalArcSpinner: {
    width: 36,
    height: 36,
    border: '3px solid rgba(255,255,255,0.10)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  },
  modalResultBtn: {
    display: 'block',
    width: '100%',
    padding: `${Gap.xl}px ${Gap.section}px`,
    background: 'none',
    border: 'none',
    borderRadius: Radius.xs,
    color: Colors.textSecondary,
    fontSize: Font.md,
    cursor: 'pointer',
    textAlign: 'left' as const,
  },
};
