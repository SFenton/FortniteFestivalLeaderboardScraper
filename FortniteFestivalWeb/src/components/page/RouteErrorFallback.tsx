/**
 * Error fallback shown when a lazy-loaded route crashes.
 * Provides a "Go to Songs" link and a "Reload" button.
 */
import { Colors, Font, Gap, Radius } from '@festival/theme';

export default function RouteErrorFallback() {
  return (
    <div style={{
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      justifyContent: 'center',
      height: '100%',
      minHeight: '60vh',
      color: Colors.textPrimary,
      textAlign: 'center',
      padding: Gap.section,
      gap: Gap.xl,
    }}>
      <h2 style={{ fontSize: Font.xl, margin: 0 }}>Something went wrong</h2>
      <p style={{ fontSize: Font.md, color: Colors.textSecondary, margin: 0 }}>
        An error occurred loading this page. Try going back or reloading.
      </p>
      <div style={{ display: 'flex', gap: Gap.md }}>
        <a
          href="#/songs"
          style={{
            padding: `${Gap.lg}px ${Gap.section}px`,
            borderRadius: Radius.xs,
            border: `1px solid ${Colors.accentBlue}`,
            backgroundColor: Colors.chipSelectedBg,
            color: Colors.textPrimary,
            fontSize: Font.md,
            fontWeight: 600,
            textDecoration: 'none',
            cursor: 'pointer',
          }}
        >
          Go to Songs
        </a>
        <button
          onClick={() => window.location.reload()}
          style={{
            padding: `${Gap.lg}px ${Gap.section}px`,
            borderRadius: Radius.xs,
            border: 'none',
            backgroundColor: Colors.accentPurple,
            color: Colors.textPrimary,
            fontSize: Font.md,
            fontWeight: 600,
            cursor: 'pointer',
          }}
        >
          Reload
        </button>
      </div>
    </div>
  );
}
