import { Colors, Font, Gap, Layout, MaxWidth } from '../theme';

export default function SettingsPage() {
  return (
    <div style={styles.page}>
      <div style={styles.stickyHeader}>
        <div style={styles.container}>
          <h1 style={styles.heading}>Settings</h1>
        </div>
      </div>
      <div style={styles.container}>
        <p style={styles.placeholder}>Settings coming soon.</p>
      </div>
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  page: {
    minHeight: '100%',
    backgroundColor: Colors.backgroundApp,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
  },
  stickyHeader: {
    position: 'sticky' as const,
    top: 0,
    backgroundColor: Colors.backgroundApp,
    zIndex: 10,
    paddingBottom: Gap.md,
  },
  container: {
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px`,
  },
  heading: {
    fontSize: Font.title,
    fontWeight: 700,
    marginBottom: Gap.xl,
  },
  placeholder: {
    fontSize: Font.md,
    color: Colors.textTertiary,
  },
};
