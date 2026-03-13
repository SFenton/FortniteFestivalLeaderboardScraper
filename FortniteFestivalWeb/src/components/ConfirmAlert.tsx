import { useEffect, useLayoutEffect, useState, useCallback } from 'react';
import { Colors, Font, Gap } from '../theme';

const TRANSITION_MS = 250;

export default function ConfirmAlert({
  title,
  message,
  onNo,
  onYes,
}: {
  title: string;
  message: string;
  onNo: () => void;
  onYes: () => void;
}) {
  const [animIn, setAnimIn] = useState(false);

  useLayoutEffect(() => {
    const id = requestAnimationFrame(() => setAnimIn(true));
    return () => cancelAnimationFrame(id);
  }, []);

  // Close on Escape
  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onNo(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [onNo]);

  return (
    <div
      style={{
        ...styles.overlay,
        opacity: animIn ? 1 : 0,
        transition: `opacity ${TRANSITION_MS}ms ease`,
      }}
      onClick={onNo}
    >
      <div
        style={{
          ...styles.card,
          opacity: animIn ? 1 : 0,
          transform: animIn ? 'scale(1)' : 'scale(0.95)',
          transition: `opacity ${TRANSITION_MS}ms ease, transform ${TRANSITION_MS}ms ease`,
        }}
        onClick={e => e.stopPropagation()}
      >
        <div style={{ ...styles.title, opacity: 0, animation: animIn ? `fadeInUp 300ms ease-out 100ms forwards` : 'none' }}>{title}</div>
        <div style={{ ...styles.message, opacity: 0, animation: animIn ? `fadeInUp 300ms ease-out 200ms forwards` : 'none' }}>{message}</div>
        <div style={{ ...styles.buttons, opacity: 0, animation: animIn ? `fadeInUp 300ms ease-out 300ms forwards` : 'none' }}>
          <button style={styles.btnNo} onClick={onNo}>No</button>
          <button style={styles.btnYes} onClick={onYes}>Yes</button>
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
    zIndex: 1100,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  card: {
    backgroundColor: Colors.surfaceFrosted,
    backdropFilter: 'blur(18px)',
    WebkitBackdropFilter: 'blur(18px)',
    borderRadius: 12,
    padding: Gap.section,
    maxWidth: 340,
    width: '90%',
    color: Colors.textPrimary,
    boxShadow: '0 8px 32px rgba(0,0,0,0.5)',
  },
  title: {
    fontSize: Font.lg,
    fontWeight: 700,
    marginBottom: Gap.md,
  },
  message: {
    fontSize: Font.md,
    color: Colors.textSecondary,
    marginBottom: Gap.section,
    lineHeight: '1.4',
  },
  buttons: {
    display: 'flex',
    gap: Gap.md,
  },
  btnNo: {
    flex: 1,
    padding: `${Gap.xl}px`,
    borderRadius: 8,
    border: `1px solid ${Colors.accentBlue}`,
    backgroundColor: Colors.chipSelectedBg,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: 600,
    cursor: 'pointer',
    textAlign: 'center' as const,
  },
  btnYes: {
    flex: 1,
    padding: `${Gap.xl}px`,
    borderRadius: 8,
    border: 'none',
    backgroundColor: Colors.statusRed,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: 600,
    cursor: 'pointer',
    textAlign: 'center' as const,
  },
};
