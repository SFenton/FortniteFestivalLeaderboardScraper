import { BrowserRouter, Routes, Route, NavLink, Navigate, useNavigate } from 'react-router-dom';
import { FestivalProvider } from './contexts/FestivalContext';
import PlayerSearch from './components/PlayerSearch';
import { useTrackedPlayer, type TrackedPlayer } from './hooks/useTrackedPlayer';
import { useSyncStatus } from './hooks/useSyncStatus';
import SongsPage from './pages/SongsPage';
import SongDetailPage from './pages/SongDetailPage';
import LeaderboardPage from './pages/LeaderboardPage';
import PlayerPage from './pages/PlayerPage';
import { Colors, Font, Gap, Radius } from './theme';

export default function App() {
  return (
    <FestivalProvider>
      <BrowserRouter>
        <AppShell />
      </BrowserRouter>
    </FestivalProvider>
  );
}

function AppShell() {
  const { player, setPlayer, clearPlayer } = useTrackedPlayer();
  const { isSyncing } = useSyncStatus(player?.accountId);
  const navigate = useNavigate();

  const handleSelect = (p: TrackedPlayer) => {
    setPlayer(p);
    navigate(`/player/${p.accountId}`);
  };

  return (
    <>
      <nav style={styles.nav}>
        <span style={styles.brand}>Festival Score Tracker</span>
        <div style={styles.navLinks}>
          <NavLink
            to="/songs"
            style={({ isActive }) => ({
              ...styles.navLink,
              ...(isActive ? styles.navLinkActive : {}),
            })}
          >
            Songs
          </NavLink>
        </div>
        <div style={styles.spacer} />
        <PlayerSearch
          player={player}
          onSelect={handleSelect}
          onClear={clearPlayer}
          isSyncing={isSyncing}
        />
      </nav>
      <Routes>
        <Route path="/" element={<Navigate to="/songs" replace />} />
        <Route path="/songs" element={<SongsPage accountId={player?.accountId} />} />
        <Route path="/songs/:songId" element={<SongDetailPage />} />
        <Route path="/songs/:songId/:instrument" element={<LeaderboardPage />} />
        <Route path="/player/:accountId" element={<PlayerPage />} />
      </Routes>
    </>
  );
}

const styles: Record<string, React.CSSProperties> = {
  nav: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.section,
    padding: `${Gap.md}px ${Gap.section}px`,
    backgroundColor: Colors.backgroundBlack,
    borderBottom: `1px solid ${Colors.borderSubtle}`,
    position: 'sticky' as const,
    top: 0,
    zIndex: 100,
  },
  brand: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.accentPurple,
  },
  navLinks: {
    display: 'flex',
    gap: Gap.md,
  },
  spacer: {
    flex: 1,
  },
  navLink: {
    color: Colors.textTertiary,
    textDecoration: 'none',
    fontSize: Font.md,
    padding: `${Gap.sm}px ${Gap.md}px`,
    borderRadius: Radius.xs,
    transition: 'color 0.15s',
  },
  navLinkActive: {
    color: Colors.textPrimary,
    backgroundColor: Colors.surfaceSubtle,
  },
};
