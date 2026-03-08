import { BrowserRouter, Routes, Route, NavLink, Navigate, useNavigate, useLocation } from 'react-router-dom';
import { useEffect, useState, useRef, useCallback } from 'react';
import { FestivalProvider, useFestival } from './contexts/FestivalContext';
import { SettingsProvider } from './contexts/SettingsContext';
import PlayerSearch from './components/PlayerSearch';
import { AnimatedBackground } from './components/AnimatedBackground';
import { useTrackedPlayer, type TrackedPlayer } from './hooks/useTrackedPlayer';
import { useSyncStatus } from './hooks/useSyncStatus';
import { useIsMobile } from './hooks/useIsMobile';
import SongsPage from './pages/SongsPage';
import SongDetailPage from './pages/SongDetailPage';
import LeaderboardPage from './pages/LeaderboardPage';
import PlayerPage from './pages/PlayerPage';
import SuggestionsPage from './pages/SuggestionsPage';
import SettingsPage from './pages/SettingsPage';
import { Colors, Font, Gap, Radius, Size } from './theme';

export default function App() {
  return (
    <SettingsProvider>
      <FestivalProvider>
        <BrowserRouter basename="/app">
          <AppShell />
        </BrowserRouter>
      </FestivalProvider>
    </SettingsProvider>
  );
}

const ANIMATED_BG_ROUTES = new Set(['/', '/songs', '/suggestions', '/settings']);
function isAnimatedBgRoute(pathname: string) {
  return ANIMATED_BG_ROUTES.has(pathname) || pathname.startsWith('/player/');
}

function AppShell() {
  const { player, setPlayer, clearPlayer } = useTrackedPlayer();
  const { isSyncing } = useSyncStatus(player?.accountId);
  const { state: { songs } } = useFestival();
  const navigate = useNavigate();
  const location = useLocation();
  const isMobile = useIsMobile();
  const [sidebarOpen, setSidebarOpen] = useState(false);

  const handleSelect = (p: TrackedPlayer) => {
    setPlayer(p);
    navigate(`/player/${p.accountId}`);
  };

  const showAnimatedBg = isAnimatedBgRoute(location.pathname);

  return (
    <div style={styles.shell}>
      <ScrollToTop />
      {showAnimatedBg && <AnimatedBackground songs={songs} />}
      <nav style={styles.nav}>
        {!isMobile && (
          <button
            style={styles.hamburger}
            onClick={() => setSidebarOpen((o) => !o)}
            aria-label="Open navigation"
          >
            <span style={styles.hamburgerLine} />
            <span style={styles.hamburgerLine} />
            <span style={styles.hamburgerLine} />
          </button>
        )}
        <div style={styles.spacer} />
        <PlayerSearch
          player={player}
          onSelect={handleSelect}
          onClear={clearPlayer}
          isSyncing={isSyncing}
        />
      </nav>

      {!isMobile && (
        <Sidebar
          player={player}
          open={sidebarOpen}
          onClose={() => setSidebarOpen(false)}
        />
      )}

      <div id="main-content" style={styles.content}>
        <Routes>
          <Route path="/" element={<Navigate to="/songs" replace />} />
          <Route path="/songs" element={<SongsPage accountId={player?.accountId} />} />
          <Route path="/songs/:songId" element={<SongDetailPage />} />
          <Route path="/songs/:songId/:instrument" element={<LeaderboardPage />} />
          <Route path="/player/:accountId" element={<PlayerPage />} />
          {player && (
            <Route path="/suggestions" element={<SuggestionsPage accountId={player.accountId} />} />
          )}
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
      </div>

      {isMobile && <BottomNav hasPlayer={!!player} />}
    </div>
  );
}

const SIDEBAR_DURATION = 250;

function Sidebar({
  player,
  open,
  onClose,
}: {
  player: TrackedPlayer | null;
  open: boolean;
  onClose: () => void;
}) {
  const sidebarRef = useRef<HTMLDivElement>(null);
  const [mounted, setMounted] = useState(false);
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    if (open) {
      setMounted(true);
      // Force a layout read so the initial transform is applied before transitioning
      requestAnimationFrame(() => {
        requestAnimationFrame(() => setVisible(true));
      });
    } else {
      setVisible(false);
    }
  }, [open]);

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
            Songs
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
              Suggestions
            </NavLink>
          )}
        </nav>
        <div style={styles.sidebarFooter}>
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

function BottomNav({ hasPlayer }: { hasPlayer: boolean }) {
  const tabs = [
    { to: '/songs', label: 'Songs', icon: '♫' },
    ...(hasPlayer ? [{ to: '/suggestions', label: 'Suggestions', icon: '★' }] : []),
    { to: '/settings', label: 'Settings', icon: '⚙' },
  ];

  return (
    <nav style={styles.bottomNav}>
      {tabs.map((tab) => (
        <NavLink
          key={tab.to}
          to={tab.to}
          style={({ isActive }) => ({
            ...styles.bottomTab,
            ...(isActive ? styles.bottomTabActive : {}),
          })}
        >
          <span style={styles.bottomTabIcon}>{tab.icon}</span>
          {tab.label}
        </NavLink>
      ))}
    </nav>
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
    if (pathname === '/suggestions') return;
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
  },
  nav: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `${Gap.md}px ${Gap.section}px`,
    backgroundColor: 'rgba(0, 0, 0, 0.75)',
    backdropFilter: 'blur(16px)',
    WebkitBackdropFilter: 'blur(16px)',
    borderBottom: `1px solid ${Colors.borderSubtle}`,
    flexShrink: 0,
    zIndex: 100,
    position: 'relative' as const,
  },
  content: {
    flex: 1,
    overflowY: 'auto' as const,
    position: 'relative' as const,
    zIndex: 1,
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
  spacer: {
    flex: 1,
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
    backgroundColor: Colors.backgroundCard,
    borderRight: `1px solid ${Colors.borderSubtle}`,
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

  // Bottom nav (mobile)
  bottomNav: {
    display: 'flex',
    justifyContent: 'space-around',
    alignItems: 'center',
    backgroundColor: 'rgba(0, 0, 0, 0.75)',
    backdropFilter: 'blur(16px)',
    WebkitBackdropFilter: 'blur(16px)',
    borderTop: `1px solid ${Colors.borderSubtle}`,
    flexShrink: 0,
    zIndex: 100,
    position: 'relative' as const,
    padding: `${Gap.sm}px 0 ${Gap.md}px`,
  },
  bottomTab: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    gap: 2,
    textDecoration: 'none',
    color: Colors.textTertiary,
    fontSize: Font.xs,
    padding: `${Gap.sm}px ${Gap.xl}px`,
    borderRadius: Radius.xs,
    transition: 'color 0.15s',
    minWidth: 64,
  },
  bottomTabActive: {
    color: Colors.accentPurple,
  },
  bottomTabIcon: {
    fontSize: 20,
    lineHeight: '24px',
  },
};
