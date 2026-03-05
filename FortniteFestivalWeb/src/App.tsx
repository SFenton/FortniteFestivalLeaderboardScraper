import { BrowserRouter, Routes, Route, NavLink, Navigate } from 'react-router-dom';
import { FestivalProvider } from './contexts/FestivalContext';
import SongsPage from './pages/SongsPage';
import SongDetailPage from './pages/SongDetailPage';
import LeaderboardPage from './pages/LeaderboardPage';
import { Colors, Font, Gap, Radius } from './theme';

export default function App() {
  return (
    <FestivalProvider>
      <BrowserRouter>
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
        </nav>
        <Routes>
          <Route path="/" element={<Navigate to="/songs" replace />} />
          <Route path="/songs" element={<SongsPage />} />
          <Route path="/songs/:songId" element={<SongDetailPage />} />
          <Route path="/songs/:songId/:instrument" element={<LeaderboardPage />} />
        </Routes>
      </BrowserRouter>
    </FestivalProvider>
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
