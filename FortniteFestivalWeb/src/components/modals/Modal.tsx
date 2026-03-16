import { useEffect, useLayoutEffect, useRef, useState, useCallback } from 'react';
import { IoChevronDown, IoClose } from 'react-icons/io5';
import {
  DndContext,
  closestCenter,
  KeyboardSensor,
  PointerSensor,
  TouchSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core';
import {
  SortableContext,
  sortableKeyboardCoordinates,
  useSortable,
  verticalListSortingStrategy,
  arrayMove,
} from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { useIsMobile } from '../../hooks/useIsMobile';
import { useScrollMask } from '../../hooks/useScrollMask';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../hooks/useVisualViewport';
import { Colors, Radius, Font, Gap } from '@festival/theme';

const TRANSITION_MS = 300;

type Props = {
  visible: boolean;
  title: string;
  onClose: () => void;
  onApply: () => void;
  onReset?: () => void;
  resetLabel?: string;
  resetHint?: string;
  applyLabel?: string;
  applyDisabled?: boolean;
  children: React.ReactNode;
};

/**
 * Adaptive modal: bottom sheet on mobile (≤768px), side flyout on desktop.
 * Uses a draft pattern — the parent controls open/close & apply/cancel.
 */
export default function Modal({ visible, title, onClose, onApply, onReset, resetLabel, resetHint, applyLabel, applyDisabled, children }: Props) {
  const isMobile = useIsMobile();
  const vvHeight = useVisualViewportHeight();
  const vvOffsetTop = useVisualViewportOffsetTop();
  const [mounted, setMounted] = useState(false);
  const [animIn, setAnimIn] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const updateScrollMask = useScrollMask(scrollRef, [visible, children]);
  const handleContentScroll = useCallback(() => { updateScrollMask(); }, [updateScrollMask]);

  useEffect(() => {
    if (visible) {
      setMounted(true);
    } else {
      setAnimIn(false);
    }
  }, [visible]);

  useLayoutEffect(() => {
    if (mounted && visible) {
      panelRef.current?.getBoundingClientRect();
      const id = requestAnimationFrame(() => setAnimIn(true));
      return () => cancelAnimationFrame(id);
    }
  }, [mounted, visible]);

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
  };

  const mobilePanel: React.CSSProperties = {
    ...panelBase,
    left: 0,
    right: 0,
    top: vvOffsetTop + vvHeight * 0.2,
    height: vvHeight * 0.8,
    borderTopLeftRadius: Radius.lg,
    borderTopRightRadius: Radius.lg,
    transition: `transform ${TRANSITION_MS}ms ease`,
    transform: animIn ? 'translateY(0)' : 'translateY(100%)',
  };

  const desktopPanel: React.CSSProperties = {
    ...panelBase,
    top: '50%',
    left: '50%',
    width: '80vw',
    maxWidth: '90vw',
    height: '70vh',
    borderRadius: Radius.lg,
    transition: `opacity ${TRANSITION_MS}ms ease, transform ${TRANSITION_MS}ms ease`,
    transform: animIn ? 'translate(-50%, -50%)' : 'translate(-50%, -40%)',
    opacity: animIn ? 1 : 0,
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
          <button style={headerStyles.closeBtn} onClick={onClose} aria-label="Close"><IoClose size={18} /></button>
        </div>

        {/* Content */}
        <div ref={scrollRef} onScroll={handleContentScroll} style={contentStyles.scroll}>
          {children}
          {onReset && (
            <ModalSection title={resetLabel ?? 'Reset'} hint={resetHint}>
              <button style={footerStyles.reset} onClick={onReset}>{resetLabel ?? 'Reset'}</button>
            </ModalSection>
          )}
        </div>

        {/* Footer */}
        <div style={footerStyles.wrap}>
          <button
            style={{ ...footerStyles.apply, ...(applyDisabled ? footerStyles.applyDisabled : {}) }}
            onClick={onApply}
            disabled={applyDisabled}
          >
            {applyLabel ?? 'Apply'}
          </button>
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
    padding: `${Gap.xl}px 16px ${Gap.xl}px ${Gap.section}px`,
    flexShrink: 0,
  },
  title: {
    fontSize: Font.xl,
    fontWeight: 700,
    margin: 0,
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
    padding: `${Gap.xl}px ${Gap.section}px`,
    flexShrink: 0,
  },
  reset: {
    width: '100%',
    background: Colors.dangerBg,
    border: `1px solid ${Colors.statusRed}`,
    borderRadius: Radius.xs,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: 600,
    padding: `${Gap.xl}px ${Gap.xl}px`,
    cursor: 'pointer',
    textAlign: 'center' as const,
  },
  apply: {
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
    transition: 'opacity 300ms ease',
  },
  applyDisabled: {
    opacity: 0.4,
    cursor: 'default',
  },
};

/* ── Reusable section + controls used by sort/filter modals ── */

export function ModalSection({ title, hint, children }: { title?: string; hint?: string; children: React.ReactNode }) {
  return (
    <div style={sectionStyles.wrap}>
      {title && <div style={sectionStyles.title}>{title}</div>}
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

export function ToggleRow({ label, description, checked, onToggle, disabled, icon, large }: { label: React.ReactNode; description?: string; checked: boolean; onToggle: () => void; disabled?: boolean; icon?: React.ReactNode; large?: boolean }) {
  return (
    <button
      style={{
        ...toggleStyles.row,
        ...(large ? toggleStyles.rowLarge : {}),
        ...(disabled ? toggleStyles.rowDisabled : {}),
      }}
      onClick={disabled ? undefined : onToggle}
      disabled={disabled}
    >
      {icon && <div style={toggleStyles.icon}>{icon}</div>}
      <div style={{ flex: 1 }}>
        <div style={{ ...toggleStyles.label, ...(large ? toggleStyles.labelLarge : {}) }}>{label}</div>
        {description && <div style={{ ...toggleStyles.desc, ...(large ? toggleStyles.descLarge : {}) }}>{description}</div>}
      </div>
      <div
        style={{
          ...(large ? toggleStyles.trackLarge : toggleStyles.track),
          ...(checked ? (large ? toggleStyles.trackLargeOn : toggleStyles.trackOn) : {}),
          ...(disabled ? toggleStyles.trackDisabled : {}),
        }}
      >
        <div
          style={{
            ...(large ? toggleStyles.thumbLarge : toggleStyles.thumb),
            ...(checked ? (large ? toggleStyles.thumbLargeOn : toggleStyles.thumbOn) : {}),
          }}
        />
      </div>
    </button>
  );
}

export function ReorderList({ items, onReorder }: { items: { key: string; label: string }[]; onReorder: (items: { key: string; label: string }[]) => void }) {
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 150, tolerance: 5 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const handleDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const oldIndex = items.findIndex(i => i.key === active.id);
    const newIndex = items.findIndex(i => i.key === over.id);
    if (oldIndex === -1 || newIndex === -1) return;
    onReorder(arrayMove(items, oldIndex, newIndex));
  };

  return (
    <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
      <SortableContext items={items.map(i => i.key)} strategy={verticalListSortingStrategy}>
        <div style={reorderStyles.list}>
          {items.map((item) => (
            <SortableRow key={item.key} item={item} />
          ))}
        </div>
      </SortableContext>
    </DndContext>
  );
}

function SortableRow({ item }: { item: { key: string; label: string } }) {
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({ id: item.key });

  const style: React.CSSProperties = {
    ...reorderStyles.row,
    transform: CSS.Transform.toString(transform),
    transition,
    zIndex: isDragging ? 10 : undefined,
    opacity: isDragging ? 0.85 : 1,
    boxShadow: isDragging ? '0 4px 16px rgba(0,0,0,0.4)' : undefined,
    cursor: isDragging ? 'grabbing' : 'grab',
  };

  return (
    <div ref={setNodeRef} style={style} {...attributes} {...listeners}>
      {/* Drag handle icon (visual indicator only) */}
      <span style={reorderStyles.dragHandle}>⠿</span>
      <span style={reorderStyles.label}>{item.label}</span>
    </div>
  );
}

/** Collapsible accordion section. */
export function Accordion({ title, hint, icon, defaultOpen = false, children }: { title: string; hint?: string; icon?: React.ReactNode; defaultOpen?: boolean; children: React.ReactNode }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div>
      <button style={accordionStyles.header} onClick={() => setOpen(o => !o)}>
        {icon && <span style={accordionStyles.icon}>{icon}</span>}
        <div style={accordionStyles.titleGroup}>
          <span style={accordionStyles.title}>{title}</span>
          {hint && <span style={accordionStyles.hint}>{hint}</span>}
        </div>
        <IoChevronDown style={{ ...accordionStyles.chevron, transform: open ? 'rotate(180deg)' : 'rotate(0deg)' }} size={16} />
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
      <button style={bulkStyles.selectBtn} onClick={onSelectAll}>Select All</button>
      <button style={bulkStyles.clearBtn} onClick={onClearAll}>Clear All</button>
    </div>
  );
}

const sectionStyles: Record<string, React.CSSProperties> = {
  wrap: {
    marginBottom: Gap.section,
  },
  title: {
    fontSize: Font.lg,
    fontWeight: 700,
    marginBottom: Gap.sm,
    color: Colors.textPrimary,
  },
  hint: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
    marginBottom: Gap.md,
    lineHeight: '1.4',
  },
};

const radioStyles: Record<string, React.CSSProperties> = {
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    width: '100%',
    padding: `${Gap.xl}px ${Gap.xl}px`,
    backgroundColor: 'transparent',
    border: 'none',
    borderRadius: Radius.xs,
    color: Colors.textSecondary,
    fontSize: Font.md,
    fontWeight: 500,
    cursor: 'pointer',
    marginBottom: Gap.xs,
    textAlign: 'left' as const,
  },
  rowSelected: {
    color: Colors.textPrimary,
  },
  dot: {
    width: 18,
    height: 18,
    borderRadius: '50%',
    borderWidth: 2,
    borderStyle: 'solid',
    borderColor: Colors.borderPrimary,
    flexShrink: 0,
    boxSizing: 'border-box' as const,
    position: 'relative' as const,
    top: 1,
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
    fontSize: Font.md,
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
    padding: `${Gap.md}px 0`,
    backgroundColor: 'transparent',
    border: 'none',
    borderRadius: 0,
    cursor: 'pointer',
    textAlign: 'left' as const,
    color: Colors.textPrimary,
    transition: 'opacity 300ms ease',
  },
  rowDisabled: {
    opacity: 0.5,
    cursor: 'default',
  },
  icon: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
  },
  label: {
    fontSize: Font.md,
    fontWeight: 600,
  },
  desc: {
    fontSize: Font.sm,
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
  trackDisabled: {
    opacity: 0.4,
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
  rowLarge: {
    padding: `${Gap.lg}px 0`,
  },
  labelLarge: {
    fontSize: Font.lg,
  },
  descLarge: {
    fontSize: Font.md,
  },
  trackLarge: {
    width: 44,
    height: 24,
    borderRadius: 12,
    backgroundColor: Colors.surfaceMuted,
    position: 'relative' as const,
    flexShrink: 0,
    transition: 'background-color 0.15s',
  },
  trackLargeOn: {
    backgroundColor: Colors.accentBlue,
  },
  thumbLarge: {
    width: 20,
    height: 20,
    borderRadius: '50%',
    backgroundColor: Colors.textPrimary,
    position: 'absolute' as const,
    top: 2,
    left: 2,
    transition: 'left 0.15s',
  },
  thumbLargeOn: {
    left: 22,
  },
};

const reorderStyles: Record<string, React.CSSProperties> = {
  list: {
    display: 'flex',
    flexDirection: 'column',
    border: `1px solid ${Colors.borderSubtle}`,
    borderRadius: Radius.xs,
    overflow: 'hidden',
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `${Gap.xl}px ${Gap.xl}px`,
    backgroundColor: Colors.surfaceSubtle,
    borderBottom: `1px solid ${Colors.borderSubtle}`,
    touchAction: 'none',
  },
  dragHandle: {
    color: Colors.textMuted,
    fontSize: Font.lg,
    flexShrink: 0,
    userSelect: 'none' as const,
    lineHeight: 1,
  },
  label: {
    flex: 1,
    fontSize: Font.md,
    fontWeight: 500,
    color: Colors.textPrimary,
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
  icon: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
  },
  title: {
    fontSize: Font.lg,
    fontWeight: 700,
  },
  hint: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
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
    paddingLeft: Gap.xl,
  },
};

const bulkStyles: Record<string, React.CSSProperties> = {
  wrap: {
    display: 'flex',
    justifyContent: 'flex-end',
    gap: Gap.md,
    marginBottom: Gap.xl,
  },
  selectBtn: {
    padding: `${Gap.sm}px ${Gap.md}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.accentBlue}`,
    backgroundColor: Colors.chipSelectedBg,
    color: Colors.textPrimary,
    fontSize: Font.sm,
    fontWeight: 600,
    cursor: 'pointer',
  },
  clearBtn: {
    padding: `${Gap.sm}px ${Gap.md}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.statusRed}`,
    backgroundColor: Colors.dangerBg,
    color: Colors.textPrimary,
    fontSize: Font.sm,
    fontWeight: 600,
    cursor: 'pointer',
  },
};
