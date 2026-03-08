import { useEffect, useRef, useState, useCallback } from 'react';
import { useIsMobile } from '../hooks/useIsMobile';
import { Colors, Radius, Font, Gap } from '../theme';

const TRANSITION_MS = 300;

type Props = {
  visible: boolean;
  title: string;
  onClose: () => void;
  onApply: () => void;
  onReset?: () => void;
  children: React.ReactNode;
};

/**
 * Adaptive modal: bottom sheet on mobile (≤768px), side flyout on desktop.
 * Uses a draft pattern — the parent controls open/close & apply/cancel.
 */
export default function Modal({ visible, title, onClose, onApply, onReset, children }: Props) {
  const isMobile = useIsMobile();
  const [mounted, setMounted] = useState(false);
  const [animIn, setAnimIn] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (visible) {
      setMounted(true);
      requestAnimationFrame(() => {
        requestAnimationFrame(() => setAnimIn(true));
      });
    } else {
      setAnimIn(false);
    }
  }, [visible]);

  const handleTransitionEnd = useCallback(() => {
    if (!animIn) setMounted(false);
  }, [animIn]);

  // Close on Escape
  useEffect(() => {
    if (!mounted) return;
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [mounted, onClose]);

  if (!mounted) return null;

  const overlayStyle: React.CSSProperties = {
    position: 'fixed',
    inset: 0,
    backgroundColor: Colors.overlayModal,
    zIndex: 1000,
    opacity: animIn ? 1 : 0,
    transition: `opacity ${TRANSITION_MS}ms ease`,
  };

  const panelBase: React.CSSProperties = {
    position: 'fixed',
    zIndex: 1001,
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: Colors.surfaceFrosted,
    backdropFilter: 'blur(18px)',
    WebkitBackdropFilter: 'blur(18px)',
    color: Colors.textPrimary,
    transition: `transform ${TRANSITION_MS}ms ease`,
  };

  const mobilePanel: React.CSSProperties = {
    ...panelBase,
    bottom: 0,
    left: 0,
    right: 0,
    height: '80vh',
    borderTopLeftRadius: Radius.lg,
    borderTopRightRadius: Radius.lg,
    transform: animIn ? 'translateY(0)' : 'translateY(100%)',
  };

  const desktopPanel: React.CSSProperties = {
    ...panelBase,
    top: 0,
    right: 0,
    bottom: 0,
    width: 440,
    maxWidth: '100vw',
    transform: animIn ? 'translateX(0)' : 'translateX(100%)',
  };

  return (
    <>
      <div style={overlayStyle} onClick={onClose} />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        style={isMobile ? mobilePanel : desktopPanel}
        onTransitionEnd={handleTransitionEnd}
      >
        {/* Header */}
        <div style={headerStyles.wrap}>
          <h2 style={headerStyles.title}>{title}</h2>
          <button style={headerStyles.closeBtn} onClick={onClose}>Cancel</button>
        </div>

        {/* Content */}
        <div style={contentStyles.scroll}>
          {children}
        </div>

        {/* Footer */}
        <div style={footerStyles.wrap}>
          {onReset && (
            <button style={footerStyles.reset} onClick={onReset}>Reset</button>
          )}
          <div style={{ flex: 1 }} />
          <button style={footerStyles.apply} onClick={onApply}>Apply</button>
        </div>
      </div>
    </>
  );
}

/* ── Shared sub-component styles ── */

const headerStyles: Record<string, React.CSSProperties> = {
  wrap: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: `${Gap.xl}px ${Gap.section}px`,
    borderBottom: `1px solid ${Colors.borderSubtle}`,
    flexShrink: 0,
  },
  title: {
    fontSize: Font.lg,
    fontWeight: 700,
    margin: 0,
  },
  closeBtn: {
    background: Colors.surfaceElevated,
    border: `1px solid ${Colors.borderPrimary}`,
    borderRadius: Radius.xs,
    color: Colors.textSecondary,
    fontSize: Font.sm,
    padding: `${Gap.sm}px ${Gap.xl}px`,
    cursor: 'pointer',
  },
};

const contentStyles: Record<string, React.CSSProperties> = {
  scroll: {
    flex: 1,
    overflowY: 'auto',
    padding: `${Gap.xl}px ${Gap.section}px`,
  },
};

const footerStyles: Record<string, React.CSSProperties> = {
  wrap: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    padding: `${Gap.xl}px ${Gap.section}px`,
    borderTop: `1px solid ${Colors.borderSubtle}`,
    flexShrink: 0,
  },
  reset: {
    background: Colors.dangerBg,
    border: `1px solid ${Colors.statusRed}`,
    borderRadius: Radius.xs,
    color: Colors.textPrimary,
    fontSize: Font.sm,
    fontWeight: 600,
    padding: `${Gap.md}px ${Gap.xl}px`,
    cursor: 'pointer',
  },
  apply: {
    background: Colors.chipSelectedBg,
    border: `1px solid ${Colors.accentBlue}`,
    borderRadius: Radius.xs,
    color: Colors.textPrimary,
    fontSize: Font.sm,
    fontWeight: 600,
    padding: `${Gap.md}px ${Gap.xl}px`,
    cursor: 'pointer',
  },
};

/* ── Reusable section + controls used by sort/filter modals ── */

export function ModalSection({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return (
    <div style={sectionStyles.wrap}>
      <div style={sectionStyles.title}>{title}</div>
      {hint && <div style={sectionStyles.hint}>{hint}</div>}
      {children}
    </div>
  );
}

export function RadioRow({ label, selected, onSelect }: { label: string; selected: boolean; onSelect: () => void }) {
  return (
    <button
      style={{
        ...radioStyles.row,
        ...(selected ? radioStyles.rowSelected : {}),
      }}
      onClick={onSelect}
    >
      <span
        style={{
          ...radioStyles.dot,
          ...(selected ? radioStyles.dotSelected : {}),
        }}
      />
      <span>{label}</span>
    </button>
  );
}

export function ChoicePill({ label, selected, onSelect }: { label: string; selected: boolean; onSelect: () => void }) {
  return (
    <button
      style={{
        ...choiceStyles.pill,
        ...(selected ? choiceStyles.pillSelected : {}),
      }}
      onClick={onSelect}
    >
      {label}
    </button>
  );
}

export function ToggleRow({ label, description, checked, onToggle }: { label: React.ReactNode; description?: string; checked: boolean; onToggle: () => void }) {
  return (
    <button style={toggleStyles.row} onClick={onToggle}>
      <div style={{ flex: 1 }}>
        <div style={toggleStyles.label}>{label}</div>
        {description && <div style={toggleStyles.desc}>{description}</div>}
      </div>
      <div
        style={{
          ...toggleStyles.track,
          ...(checked ? toggleStyles.trackOn : {}),
        }}
      >
        <div
          style={{
            ...toggleStyles.thumb,
            ...(checked ? toggleStyles.thumbOn : {}),
          }}
        />
      </div>
    </button>
  );
}

export function ReorderList({ items, onReorder }: { items: { key: string; label: string }[]; onReorder: (items: { key: string; label: string }[]) => void }) {
  const moveUp = (idx: number) => {
    if (idx <= 0) return;
    const next = [...items];
    const tmp = next[idx - 1]!;
    next[idx - 1] = next[idx]!;
    next[idx] = tmp;
    onReorder(next);
  };
  const moveDown = (idx: number) => {
    if (idx >= items.length - 1) return;
    const next = [...items];
    const tmp = next[idx]!;
    next[idx] = next[idx + 1]!;
    next[idx + 1] = tmp;
    onReorder(next);
  };

  return (
    <div style={reorderStyles.list}>
      {items.map((item, idx) => (
        <div key={item.key} style={reorderStyles.row}>
          <span style={reorderStyles.rank}>{idx + 1}</span>
          <span style={reorderStyles.label}>{item.label}</span>
          <div style={reorderStyles.arrows}>
            <button
              style={{
                ...reorderStyles.arrowBtn,
                ...(idx === 0 ? reorderStyles.arrowBtnDisabled : {}),
              }}
              onClick={() => moveUp(idx)}
              disabled={idx === 0}
              aria-label={`Move ${item.label} up`}
            >
              ▲
            </button>
            <button
              style={{
                ...reorderStyles.arrowBtn,
                ...(idx === items.length - 1 ? reorderStyles.arrowBtnDisabled : {}),
              }}
              onClick={() => moveDown(idx)}
              disabled={idx === items.length - 1}
              aria-label={`Move ${item.label} down`}
            >
              ▼
            </button>
          </div>
        </div>
      ))}
    </div>
  );
}

/** Collapsible accordion section. */
export function Accordion({ title, hint, defaultOpen = false, children }: { title: string; hint?: string; defaultOpen?: boolean; children: React.ReactNode }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div>
      <button style={accordionStyles.header} onClick={() => setOpen(o => !o)}>
        <div style={accordionStyles.titleGroup}>
          <span style={accordionStyles.title}>{title}</span>
          {hint && <span style={accordionStyles.hint}>{hint}</span>}
        </div>
        <svg style={{ ...accordionStyles.chevron, transform: open ? 'rotate(180deg)' : 'rotate(0deg)' }} width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="6 9 12 15 18 9" /></svg>
      </button>
      <div style={{ ...accordionStyles.bodyWrap, gridTemplateRows: open ? '1fr' : '0fr' }}>
        <div style={accordionStyles.bodyInner}>{children}</div>
      </div>
    </div>
  );
}

/** Bulk actions bar for multi-select filter groups. */
export function BulkActions({ onSelectAll, onClearAll }: { onSelectAll: () => void; onClearAll: () => void }) {
  return (
    <div style={bulkStyles.wrap}>
      <button style={bulkStyles.btn} onClick={onSelectAll}>Select All</button>
      <button style={bulkStyles.btn} onClick={onClearAll}>Clear All</button>
    </div>
  );
}

const sectionStyles: Record<string, React.CSSProperties> = {
  wrap: {
    marginBottom: Gap.section,
  },
  title: {
    fontSize: Font.md,
    fontWeight: 700,
    marginBottom: Gap.sm,
    color: Colors.textPrimary,
  },
  hint: {
    fontSize: Font.xs,
    color: Colors.textMuted,
    marginBottom: Gap.md,
    lineHeight: '1.4',
  },
};

const radioStyles: Record<string, React.CSSProperties> = {
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    width: '100%',
    padding: `${Gap.md}px ${Gap.xl}px`,
    backgroundColor: 'transparent',
    borderWidth: 1,
    borderStyle: 'solid',
    borderColor: Colors.borderSubtle,
    borderRadius: Radius.xs,
    color: Colors.textSecondary,
    fontSize: Font.sm,
    cursor: 'pointer',
    marginBottom: Gap.xs,
    textAlign: 'left' as const,
  },
  rowSelected: {
    backgroundColor: Colors.chipSelectedBgSubtle,
    borderColor: Colors.accentBlue,
    color: Colors.textPrimary,
  },
  dot: {
    width: 14,
    height: 14,
    borderRadius: '50%',
    borderWidth: 2,
    borderStyle: 'solid',
    borderColor: Colors.borderPrimary,
    flexShrink: 0,
    boxSizing: 'border-box' as const,
  },
  dotSelected: {
    borderColor: Colors.accentBlue,
    backgroundColor: Colors.accentBlue,
    boxShadow: `inset 0 0 0 2px ${Colors.surfaceFrosted}`,
  },
};

const choiceStyles: Record<string, React.CSSProperties> = {
  pill: {
    padding: `${Gap.sm}px ${Gap.xl}px`,
    borderRadius: Radius.xs,
    borderWidth: 1,
    borderStyle: 'solid',
    borderColor: Colors.borderPrimary,
    backgroundColor: Colors.transparent,
    color: Colors.textTertiary,
    fontSize: Font.sm,
    cursor: 'pointer',
  },
  pillSelected: {
    backgroundColor: Colors.chipSelectedBg,
    color: Colors.accentBlue,
    borderColor: Colors.accentBlue,
  },
};

const toggleStyles: Record<string, React.CSSProperties> = {
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    width: '100%',
    padding: `${Gap.md}px ${Gap.xl}px`,
    backgroundColor: 'transparent',
    borderWidth: 1,
    borderStyle: 'solid',
    borderColor: Colors.borderSubtle,
    borderRadius: Radius.xs,
    cursor: 'pointer',
    marginBottom: Gap.xs,
    textAlign: 'left' as const,
    color: Colors.textPrimary,
  },
  label: {
    fontSize: Font.sm,
    fontWeight: 600,
  },
  desc: {
    fontSize: Font.xs,
    color: Colors.textMuted,
    marginTop: Gap.xs,
  },
  track: {
    width: 36,
    height: 20,
    borderRadius: 10,
    backgroundColor: Colors.surfaceMuted,
    position: 'relative' as const,
    flexShrink: 0,
    transition: 'background-color 0.15s',
  },
  trackOn: {
    backgroundColor: Colors.accentBlue,
  },
  thumb: {
    width: 16,
    height: 16,
    borderRadius: '50%',
    backgroundColor: Colors.textPrimary,
    position: 'absolute' as const,
    top: 2,
    left: 2,
    transition: 'left 0.15s',
  },
  thumbOn: {
    left: 18,
  },
};

const reorderStyles: Record<string, React.CSSProperties> = {
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: Gap.xs,
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    padding: `${Gap.sm}px ${Gap.xl}px`,
    border: `1px solid ${Colors.borderSubtle}`,
    borderRadius: Radius.xs,
    backgroundColor: Colors.surfaceSubtle,
  },
  rank: {
    fontSize: Font.xs,
    color: Colors.textMuted,
    width: 18,
    textAlign: 'center' as const,
    flexShrink: 0,
  },
  label: {
    flex: 1,
    fontSize: Font.sm,
    color: Colors.textPrimary,
  },
  arrows: {
    display: 'flex',
    flexDirection: 'column',
    gap: 2,
  },
  arrowBtn: {
    background: 'none',
    border: 'none',
    color: Colors.textSecondary,
    fontSize: 10,
    cursor: 'pointer',
    padding: '0 4px',
    lineHeight: 1,
  },
  arrowBtnDisabled: {
    color: Colors.textDisabled,
    cursor: 'default',
  },
};

const accordionStyles: Record<string, React.CSSProperties> = {
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    width: '100%',
    padding: `${Gap.md}px 0`,
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    color: Colors.textPrimary,
    textAlign: 'left' as const,
  },
  titleGroup: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
    flex: 1,
  },
  title: {
    fontSize: Font.md,
    fontWeight: 700,
  },
  hint: {
    fontSize: Font.xs,
    color: Colors.textMuted,
  },
  chevron: {
    flexShrink: 0,
    transition: 'transform 0.2s ease',
    color: Colors.textMuted,
  },
  bodyWrap: {
    display: 'grid',
    transition: 'grid-template-rows 0.2s ease',
  },
  bodyInner: {
    overflow: 'hidden',
    minHeight: 0,
  },
};

const bulkStyles: Record<string, React.CSSProperties> = {
  wrap: {
    display: 'flex',
    gap: Gap.md,
    marginBottom: Gap.md,
  },
  btn: {
    padding: `${Gap.xs}px ${Gap.md}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.transparent,
    color: Colors.textTertiary,
    fontSize: Font.xs,
    cursor: 'pointer',
  },
};
