import { BrowserRouter, Routes, Route, NavLink, Link, Navigate, useLocation, useNavigate } from 'react-router-dom';
import { IoMusicalNotes, IoSparkles, IoStatsChart, IoPerson, IoSettings, IoSearch } from 'react-icons/io5';
import { useEffect, useLayoutEffect, useState, useMemo, useRef, useCallback } from 'react';
import { FestivalProvider, useFestival } from './contexts/FestivalContext';
import { SettingsProvider } from './contexts/SettingsContext';
import { AnimatedBackground } from './components/AnimatedBackground';
import { useTrackedPlayer, type TrackedPlayer } from './hooks/useTrackedPlayer';
import { PlayerDataProvider } from './contexts/PlayerDataContext';
import { api } from './api/client';
import type { AccountSearchResult } from './models';
import { useIsMobile } from './hooks/useIsMobile';
import SongsPage from './pages/SongsPage';
import SongDetailPage from './pages/SongDetailPage';
import LeaderboardPage from './pages/LeaderboardPage';
import PlayerPage from './pages/PlayerPage';
import SuggestionsPage from './pages/SuggestionsPage';
import SettingsPage from './pages/SettingsPage';
import { Colors, Font, Gap, Layout, Radius, Size } from './theme';
import { resetSongSettingsForDeselect } from './components/songSettings';
import BackLink from './components/BackLink';

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

const ANIMATED_BG_ROUTES = new Set(['/', '/songs', '/suggestions', '/statistics', '/settings']);
function isAnimatedBgRoute(pathname: string) {
  return ANIMATED_BG_ROUTES.has(pathname) || pathname.startsWith('/player/');
}

function AppShell() {
  const { player, setPlayer, clearPlayer } = useTrackedPlayer();
  const { state: { songs } } = useFestival();
  const location = useLocation();
  const isMobile = useIsMobile();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [playerModalOpen, setPlayerModalOpen] = useState(false);

  const handleSelect = (p: TrackedPlayer) => {
    setPlayer(p);
  };

  const handleDeselect = useCallback(() => {
    resetSongSettingsForDeselect();
    clearPlayer();
  }, [clearPlayer]);

  const showAnimatedBg = isAnimatedBgRoute(location.pathname);

  // Hierarchical back-navigation fallback for detail pages
  const backFallback = useMemo(() => {
    const path = location.pathname;
    const parts = path.split('/').filter(Boolean);
    if (parts[0] === 'songs' && parts.length === 3) return `/songs/${parts[1]}`;
    if (parts[0] === 'songs' && parts.length === 2) return '/songs';
    if (parts[0] === 'player' && parts.length === 2) return '/songs';
    return null;
  }, [location.pathname]);

  return (
    <PlayerDataProvider accountId={player?.accountId}>
    <div style={styles.shell}>
      <ScrollToTop />
      {showAnimatedBg && <AnimatedBackground songs={songs} />}
        <nav style={styles.nav}>
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
            onClick={() => setPlayerModalOpen(true)}
            aria-label="Profile"
          >
            <span style={player ? styles.headerProfileCircle : styles.headerProfileCircleEmpty}>
              <IoPerson size={16} />
            </span>
          </button>
        </nav>

      {backFallback && <BackLink fallback={backFallback} />}

      <Sidebar
        player={player}
        open={sidebarOpen}
        onClose={() => setSidebarOpen(false)}
        onDeselect={handleDeselect}
        onSelect={handleSelect}
      />

      <div id="main-content" key={player?.accountId ?? 'none'} style={styles.content}>
        <Routes>
          <Route path="/" element={<Navigate to="/songs" replace />} />
          <Route path="/songs" element={<SongsPage />} />
          <Route path="/songs/:songId" element={<SongDetailPage />} />
          <Route path="/songs/:songId/:instrument" element={<LeaderboardPage />} />
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

      {isMobile && <BottomNav player={player} />}
      <MobilePlayerSearchModal
        visible={playerModalOpen}
        onClose={() => setPlayerModalOpen(false)}
        onSelect={handleSelect}
        player={player}
        onDeselect={handleDeselect}
        isMobile={isMobile}
      />
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
      <div style={styles.headerSearchInputWrap}>
        <IoSearch size={14} style={{ color: Colors.textTertiary, flexShrink: 0 }} />
        <input
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
  onSelect,
}: {
  player: TrackedPlayer | null;
  open: boolean;
  onClose: () => void;
  onDeselect: () => void;
  onSelect: (p: TrackedPlayer) => void;
}) {
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
          {player && (
            <NavLink
              to="/statistics"
              onClick={onClose}
              style={({ isActive }) => ({
                ...styles.sidebarLink,
                ...(isActive ? styles.sidebarLinkActive : {}),
              })}
            >
              Statistics
            </NavLink>
          )}
        </nav>
        <div style={styles.sidebarFooter}>
          {player ? (
            <div style={styles.sidebarPlayerRow}>
              <Link
                to={`/player/${player.accountId}`}
                state={{ backTo: location.pathname }}
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
            <SidebarPlayerSearch onSelect={(p) => { onSelect(p); }} />
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

const ACCORDION_DURATION = 200;

function SidebarPlayerSearch({ onSelect }: { onSelect: (p: TrackedPlayer) => void }) {
  const [expanded, setExpanded] = useState(false);
  const [contentReady, setContentReady] = useState(false);
  const [dismissing, setDismissing] = useState(false);
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<AccountSearchResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [resultSeq, setResultSeq] = useState(0);
  const pendingSelectRef = useRef<TrackedPlayer | null>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const inputRef = useRef<HTMLInputElement>(null);

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
      return;
    }
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      void search(value.trim());
    }, 300);
  };

  const handleSelect = (r: AccountSearchResult) => {
    if (dismissing) return;
    pendingSelectRef.current = { accountId: r.accountId, displayName: r.displayName };
    setDismissing(true);
    // Stagger out: results at 0ms, search bar at 150ms, each 400ms anim
    // After content fades, collapse accordion then fire onSelect
    setTimeout(() => {
      setExpanded(false);
      setContentReady(false);
      setDismissing(false);
      setQuery('');
      setResults([]);
      setTimeout(() => {
        if (pendingSelectRef.current) {
          onSelect(pendingSelectRef.current);
          pendingSelectRef.current = null;
        }
      }, ACCORDION_DURATION);
    }, 550);
  };

  const toggle = () => {
    if (dismissing) return;
    if (expanded) {
      // Closing: stagger out then collapse
      setDismissing(true);
      setTimeout(() => {
        setExpanded(false);
        setContentReady(false);
        setDismissing(false);
        setQuery('');
        setResults([]);
      }, 550);
    } else {
      // Opening
      setExpanded(true);
      setTimeout(() => {
        setContentReady(true);
        inputRef.current?.focus();
      }, ACCORDION_DURATION);
    }
  };

  const stagger = (delayMs: number, dismissDelayMs?: number): React.CSSProperties =>
    dismissing
      ? { animation: `fadeOutDown 400ms ease-in ${dismissDelayMs ?? delayMs}ms forwards` }
      : contentReady
        ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards` }
        : { opacity: 0 };

  return (
    <div>
      <button style={styles.selectPlayerBtn} onClick={toggle}>
        <span style={styles.profileCircle}>
          <IoPerson size={14} />
        </span>
        Select Player
        <span style={{
          ...styles.accordionChevron,
          transform: expanded ? 'rotate(180deg)' : 'rotate(0deg)',
        }}>▾</span>
      </button>
      <div style={{
        overflow: 'hidden',
        maxHeight: expanded ? 360 : 0,
        transition: `max-height ${ACCORDION_DURATION}ms ease`,
      }}>
        <div style={styles.accordionBody}>
          <div style={stagger(0, 150)}>
            <input
              ref={inputRef}
              style={styles.sidebarSearchInput}
              placeholder="Search player…"
              value={query}
              onChange={(e) => handleChange(e.target.value)}
            />
          </div>
          <div style={{ ...styles.accordionResults, ...stagger(150, 0) }}>
            {loading && (
              <div style={styles.sidebarSpinnerWrap}>
                <div style={styles.sidebarArcSpinner} />
              </div>
            )}
            {!loading && query.length < 2 && (
              <div style={styles.sidebarSearchHintCentered}>Enter a username to search for.</div>
            )}
            {!loading && query.length >= 2 && results.length === 0 && (
              <div style={styles.sidebarSearchHintCentered}>No matching username found.</div>
            )}
            {!loading && results.map((r, i) => (
              <button
                key={`${resultSeq}-${r.accountId}`}
                style={{
                  ...styles.sidebarSearchResult,
                  opacity: 0,
                  animation: `fadeInUp 300ms ease-out ${i * 50}ms forwards`,
                }}
                onClick={() => handleSelect(r)}
              >
                {r.displayName}
              </button>
            ))}
          </div>
        </div>
      </div>
    </div>
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
}: {
  visible: boolean;
  onClose: () => void;
  onSelect: (p: TrackedPlayer) => void;
  player: TrackedPlayer | null;
  onDeselect: () => void;
  isMobile: boolean;
}) {
  const [mounted, setMounted] = useState(false);
  const [animIn, setAnimIn] = useState(false);
  const [contentReady, setContentReady] = useState(false);
  const [dismissing, setDismissing] = useState(false);
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<AccountSearchResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [resultSeq, setResultSeq] = useState(0);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const inputRef = useRef<HTMLInputElement>(null);

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
            ? { bottom: 0, left: 0, right: 0, height: '80vh', borderTopLeftRadius: Radius.lg, borderTopRightRadius: Radius.lg }
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
          <h2 style={styles.modalTitle}>Profile</h2>
          <button style={styles.modalCloseBtn} onClick={onClose}>Cancel</button>
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
              <div style={stagger(0)}>
                <input
                  ref={inputRef}
                  style={styles.modalSearchInput}
                  placeholder="Search player…"
                  value={query}
                  onChange={(e) => handleChange(e.target.value)}
                />
              </div>
              <div style={{ ...styles.modalResults, ...stagger(150) }}>
                {loading && (
                  <div style={styles.modalSpinnerWrap}>
                    <div style={styles.modalArcSpinner} />
                  </div>
                )}
                {!loading && query.length < 2 && (
                  <div style={styles.modalHintCenter}>Enter a username to search for.</div>
                )}
                {!loading && query.length >= 2 && results.length === 0 && (
                  <div style={styles.modalHintCenter}>No matching username found.</div>
                )}
                {!loading && results.map((r, i) => (
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

function BottomNav({ player }: { player: TrackedPlayer | null }) {
  const tabs: { to: string; label: string; icon: React.ReactNode }[] = [
    { to: '/songs', label: 'Songs', icon: <IoMusicalNotes size={20} /> },
    ...(player ? [{ to: '/suggestions', label: 'Suggestions', icon: <IoSparkles size={20} /> }] : []),
    ...(player ? [{ to: '/statistics', label: 'Statistics', icon: <IoStatsChart size={20} /> }] : []),
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
      <NavLink
        to="/settings"
        style={({ isActive }) => ({
          ...styles.bottomTab,
          ...(isActive ? styles.bottomTabActive : {}),
        })}
      >
        <span style={styles.bottomTabIcon}><IoSettings size={20} /></span>
        Settings
      </NavLink>
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
    if (pathname === '/suggestions' || pathname === '/songs') return;
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
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px ${Gap.md}px`,
    backgroundColor: 'transparent',
    flexShrink: 0,
    zIndex: 100,
    position: 'relative' as const,
  },
  content: {
    flex: 1,
    overflowY: 'auto' as const,
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
    marginLeft: -6,
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
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderRadius: Radius.full,
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(18px) saturate(1.4)',
    WebkitBackdropFilter: 'blur(18px) saturate(1.4)',
    border: `1px solid ${Colors.glassBorder}`,
  },
  headerSearchInput: {
    flex: 1,
    background: 'none',
    border: 'none',
    outline: 'none',
    color: Colors.textPrimary,
    fontSize: Font.sm,
  },
  headerSearchDropdown: {
    position: 'absolute' as const,
    top: '100%',
    left: 0,
    right: 0,
    marginTop: Gap.sm,
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(24px) saturate(1.4)',
    WebkitBackdropFilter: 'blur(24px) saturate(1.4)',
    border: `1px solid ${Colors.glassBorder}`,
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
    backgroundColor: Colors.glassCard,
    backdropFilter: 'blur(24px) saturate(1.4)',
    WebkitBackdropFilter: 'blur(24px) saturate(1.4)',
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
  headerProfileBtn: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    padding: 0,
    marginRight: -4,
  },
  headerProfileCircle: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 48,
    height: 48,
    borderRadius: '50%',
    backgroundColor: Colors.surfaceSubtle,
    border: `1px solid ${Colors.borderSubtle}`,
    color: Colors.textSecondary,
  },
  headerProfileCircleEmpty: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 48,
    height: 48,
    borderRadius: '50%',
    backgroundColor: '#D0D5DD',
    border: 'none',
    color: '#4A5568',
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
  selectPlayerBtn: {
    display: 'flex',
    alignItems: 'center',
    width: '100%',
    padding: `${Gap.xl}px ${Gap.section}px`,
    background: 'none',
    border: 'none',
    color: Colors.textTertiary,
    fontSize: Font.md,
    cursor: 'pointer',
    textAlign: 'left' as const,
  },
  accordionChevron: {
    marginLeft: 'auto',
    transition: `transform ${ACCORDION_DURATION}ms ease`,
    fontSize: Font.md,
  },
  accordionBody: {
    padding: `0 ${Gap.section}px ${Gap.md}px`,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.sm,
  },
  accordionResults: {
    height: 280,
    overflowY: 'auto' as const,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
  },
  sidebarSearchInput: {
    width: '100%',
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.backgroundCard,
    color: Colors.textPrimary,
    fontSize: Font.sm,
    outline: 'none',
    boxSizing: 'border-box' as const,
  },
  sidebarSearchResult: {
    display: 'block',
    width: '100%',
    padding: `${Gap.md}px ${Gap.xl}px`,
    background: 'none',
    border: 'none',
    borderRadius: Radius.xs,
    color: Colors.textSecondary,
    fontSize: Font.sm,
    cursor: 'pointer',
    textAlign: 'left' as const,
    transition: 'background-color 0.15s, color 0.15s',
  },
  sidebarSearchHintCentered: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: '100%',
    color: Colors.textTertiary,
    fontSize: Font.xs,
    textAlign: 'center' as const,
  },
  sidebarSpinnerWrap: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: '100%',
  },
  sidebarArcSpinner: {
    width: 28,
    height: 28,
    border: '3px solid rgba(255,255,255,0.10)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  },

  // Bottom nav (mobile)
  bottomNav: {
    display: 'flex',
    justifyContent: 'space-around',
    alignItems: 'center',
    backgroundColor: Colors.glassNav,
    backdropFilter: 'blur(24px) saturate(1.4)',
    WebkitBackdropFilter: 'blur(24px) saturate(1.4)',
    borderTop: `1px solid ${Colors.glassBorder}`,
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

  // Mobile player search modal
  modalHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: `${Gap.xl}px ${Gap.section}px`,
    borderBottom: `1px solid ${Colors.borderSubtle}`,
    flexShrink: 0,
  },
  modalTitle: {
    fontSize: Font.lg,
    fontWeight: 700,
    margin: 0,
  },
  modalCloseBtn: {
    background: Colors.surfaceElevated,
    border: `1px solid ${Colors.borderPrimary}`,
    borderRadius: Radius.xs,
    color: Colors.textSecondary,
    fontSize: Font.sm,
    padding: `${Gap.sm}px ${Gap.xl}px`,
    cursor: 'pointer',
  },
  modalBody: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column' as const,
    padding: Gap.section,
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
  modalSearchInput: {
    width: '100%',
    padding: `${Gap.xl}px ${Gap.section}px`,
    borderRadius: Radius.sm,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.backgroundCard,
    color: Colors.textPrimary,
    fontSize: Font.md,
    outline: 'none',
    boxSizing: 'border-box' as const,
    flexShrink: 0,
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
  modalHintCenter: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flex: 1,
    color: Colors.textTertiary,
    fontSize: Font.sm,
    textAlign: 'center' as const,
  },
  modalSpinnerWrap: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flex: 1,
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
