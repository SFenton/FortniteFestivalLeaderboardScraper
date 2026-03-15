import { useEffect, useLayoutEffect, useState, useRef, useCallback } from 'react';
import { IoClose } from 'react-icons/io5';
import { APP_VERSION, changelog, type ChangelogEntry } from '../changelog';
import { useScrollMask } from '../hooks/useScrollMask';
import { Colors, Font, Gap, Radius } from '../theme';

const TRANSITION_MS = 300;

export default function ChangelogModal({ onDismiss }: { onDismiss: () => void }) {
  const [animIn, setAnimIn] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    const id = requestAnimationFrame(() => setAnimIn(true));
    return () => cancelAnimationFrame(id);
  }, []);

  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onDismiss(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [onDismiss]);

  const updateMask = useScrollMask(scrollRef, [animIn]);
  const handleScroll = useCallback(() => updateMask(), [updateMask]);

  return (
    <div
      style={{
        ...styles.overlay,
        opacity: animIn ? 1 : 0,
        transition: `opacity ${TRANSITION_MS}ms ease`,
      }}
      onClick={onDismiss}
    >
      <div
        style={{
          ...styles.card,
          opacity: animIn ? 1 : 0,
          transform: animIn ? 'scale(1) translateY(0)' : 'scale(0.95) translateY(10px)',
          transition: `opacity ${TRANSITION_MS}ms ease, transform ${TRANSITION_MS}ms ease`,
        }}
        onClick={e => e.stopPropagation()}
      >
        {/* Header */}
        <div style={styles.header}>
          <h2 style={styles.title}>Changelog <span style={styles.dot}>·</span> {APP_VERSION}</h2>
          <button style={styles.closeBtn} onClick={onDismiss} aria-label="Close">
            <IoClose size={18} />
          </button>
        </div>

        {/* Scrollable content */}
        <div ref={scrollRef} onScroll={handleScroll} style={styles.content}>
          {changelog.map((entry: ChangelogEntry, ei) => (
            <div key={ei} style={styles.entry}>
              {entry.sections.map((section, si) => (
                <div key={si} style={si > 0 ? { marginTop: Gap.section } : undefined}>
                  <div style={styles.sectionTitle}>{section.title}</div>
                  <ul style={styles.changeList}>
                    {section.items.map((item, i) => (
                      <li key={i} style={styles.changeItem}>{item}</li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          ))}
        </div>

        {/* Footer */}
        <div style={styles.footer}>
          <button style={styles.dismissBtn} onClick={onDismiss}>
            Dismiss
          </button>
        </div>
      </div>
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  overlay: {
    position: 'fixed',
    inset: 0,
    backgroundColor: 'rgba(0,0,0,0.6)',
    zIndex: 1200,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: Gap.section,
  },
  card: {
    backgroundColor: Colors.surfaceFrosted,
    backdropFilter: 'blur(18px)',
    WebkitBackdropFilter: 'blur(18px)',
    borderRadius: Radius.lg,
    width: '100%',
    maxWidth: 520,
    maxHeight: '80vh',
    display: 'flex',
    flexDirection: 'column' as const,
    color: Colors.textPrimary,
    boxShadow: '0 8px 32px rgba(0,0,0,0.5)',
    overflow: 'hidden',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: `${Gap.xl}px 16px ${Gap.xl}px ${Gap.section}px`,
    flexShrink: 0,
  },
  title: {
    fontSize: Font.xl,
    fontWeight: 700,
    margin: 0,
  },
  dot: {
    color: Colors.textTertiary,
    margin: `0 ${Gap.xs}px`,
  },
  closeBtn: {
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
  content: {
    flex: 1,
    overflowY: 'auto' as const,
    padding: `0 ${Gap.section}px`,
  },
  entry: {
    marginBottom: Gap.section,
  },
  entryHeader: {
    display: 'flex',
    alignItems: 'baseline',
    gap: Gap.md,
    marginBottom: Gap.md,
  },
  entryVersion: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.accentBlueBright,
  },
  entryDate: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
  },
  changeList: {
    margin: 0,
    paddingLeft: Gap.section,
  },
  sectionTitle: {
    fontSize: Font.md,
    fontWeight: 700,
    color: Colors.textPrimary,
    marginBottom: Gap.sm,
    textTransform: 'uppercase' as const,
    letterSpacing: 0.5,
  },
  changeItem: {
    fontSize: Font.md,
    color: Colors.textSecondary,
    lineHeight: '1.6',
    marginBottom: Gap.sm,
  },
  footer: {
    padding: `${Gap.xl}px ${Gap.section}px`,
    flexShrink: 0,
  },
  dismissBtn: {
    width: '100%',
    background: Colors.chipSelectedBg,
    border: `1px solid ${Colors.accentBlue}`,
    borderRadius: Radius.xs,
    color: Colors.textPrimary,
    fontSize: Font.lg,
    fontWeight: 700,
    padding: `${Gap.xl}px ${Gap.xl}px`,
    cursor: 'pointer',
    textAlign: 'center' as const,
  },
};
