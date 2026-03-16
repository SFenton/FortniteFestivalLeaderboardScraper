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
import css from './Modal.module.css';

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

  const panelStyle: React.CSSProperties = isMobile
    ? {
        top: vvOffsetTop + vvHeight * 0.2,
        height: vvHeight * 0.8,
        transform: animIn ? 'translateY(0)' : 'translateY(100%)',
      }
    : {
        transform: animIn ? 'translate(-50%, -50%)' : 'translate(-50%, -40%)',
        opacity: animIn ? 1 : 0,
      };

  return (
    <>
      <div
        className={css.overlay}
        style={{ '--modal-transition-ms': `${TRANSITION_MS}ms`, opacity: animIn ? 1 : 0 } as React.CSSProperties}
        onClick={onClose}
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className={isMobile ? css.panelMobile : css.panelDesktop}
        style={{ '--modal-transition-ms': `${TRANSITION_MS}ms`, ...panelStyle } as React.CSSProperties}
        onTransitionEnd={handleTransitionEnd}
      >
        {/* Header */}
        <div className={css.headerWrap}>
          <h2 className={css.headerTitle}>{title}</h2>
          <button className={css.closeBtn} onClick={onClose} aria-label="Close"><IoClose size={18} /></button>
        </div>

        {/* Content */}
        <div ref={scrollRef} onScroll={handleContentScroll} className={css.contentScroll}>
          {children}
          {onReset && (
            <ModalSection title={resetLabel ?? 'Reset'} hint={resetHint}>
              <button className={css.resetBtn} onClick={onReset}>{resetLabel ?? 'Reset'}</button>
            </ModalSection>
          )}
        </div>

        {/* Footer */}
        <div className={css.footerWrap}>
          <button
            className={`${css.applyBtn} ${applyDisabled ? css.applyBtnDisabled : ''}`}
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

/* ── Reusable section + controls used by sort/filter modals ── */

export function ModalSection({ title, hint, children }: { title?: string; hint?: string; children: React.ReactNode }) {
  return (
    <div className={css.sectionWrap}>
      {title && <div className={css.sectionTitle}>{title}</div>}
      {hint && <div className={css.sectionHint}>{hint}</div>}
      {children}
    </div>
  );
}

export function RadioRow({ label, selected, onSelect }: { label: string; selected: boolean; onSelect: () => void }) {
  return (
    <button
      className={selected ? css.radioRowSelected : css.radioRow}
      onClick={onSelect}
    >
      <span className={selected ? css.radioDotSelected : css.radioDot} />
      <span>{label}</span>
    </button>
  );
}

export function ChoicePill({ label, selected, onSelect }: { label: string; selected: boolean; onSelect: () => void }) {
  return (
    <button
      className={selected ? css.choicePillSelected : css.choicePill}
      onClick={onSelect}
    >
      {label}
    </button>
  );
}

export function ToggleRow({ label, description, checked, onToggle, disabled, icon, large }: { label: React.ReactNode; description?: string; checked: boolean; onToggle: () => void; disabled?: boolean; icon?: React.ReactNode; large?: boolean }) {
  const rowClass = `${large ? css.toggleRowLarge : css.toggleRow} ${disabled ? css.toggleRowDisabled : ''}`;
  const trackClass = `${large ? css.toggleTrackLarge : css.toggleTrack} ${checked ? (large ? css.toggleTrackOn : css.toggleTrackOn) : ''} ${disabled ? css.toggleTrackDisabled : ''}`;
  const thumbClass = `${large ? css.toggleThumbLarge : css.toggleThumb} ${checked ? (large ? css.toggleThumbLargeOn : css.toggleThumbOn) : ''}`;

  return (
    <button
      className={rowClass}
      onClick={disabled ? undefined : onToggle}
      disabled={disabled}
    >
      {icon && <div className={css.toggleIcon}>{icon}</div>}
      <div className={css.toggleContent}>
        <div className={`${css.toggleLabel} ${large ? css.toggleLabelLarge : ''}`}>{label}</div>
        {description && <div className={`${css.toggleDesc} ${large ? css.toggleDescLarge : ''}`}>{description}</div>}
      </div>
      <div className={trackClass}>
        <div className={thumbClass} />
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
    transform: CSS.Transform.toString(transform),
    transition,
    zIndex: isDragging ? 10 : undefined,
    opacity: isDragging ? 0.85 : 1,
    boxShadow: isDragging ? '0 4px 16px rgba(0,0,0,0.4)' : undefined,
    cursor: isDragging ? 'grabbing' : 'grab',
  };

  return (
    <div ref={setNodeRef} className={css.reorderRow} style={style} {...attributes} {...listeners}>
      <span className={css.dragHandle}>⠿</span>
      <span className={css.reorderLabel}>{item.label}</span>
    </div>
  );
}

/** Collapsible accordion section. */
export function Accordion({ title, hint, icon, defaultOpen = false, children }: { title: string; hint?: string; icon?: React.ReactNode; defaultOpen?: boolean; children: React.ReactNode }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div>
      <button className={css.accordionHeader} onClick={() => setOpen(o => !o)}>
        {icon && <span className={css.accordionIcon}>{icon}</span>}
        <div className={css.accordionTitleGroup}>
          <span className={css.accordionTitle}>{title}</span>
          {hint && <span className={css.accordionHint}>{hint}</span>}
        </div>
        <IoChevronDown className={css.accordionChevron} style={{ transform: open ? 'rotate(180deg)' : 'rotate(0deg)' }} size={16} />
      </button>
      <div className={css.accordionBodyWrap} style={{ gridTemplateRows: open ? '1fr' : '0fr' }}>
        <div className={css.accordionBodyInner}>{children}</div>
      </div>
    </div>
  );
}

/** Bulk actions bar for multi-select filter groups. */
export function BulkActions({ onSelectAll, onClearAll }: { onSelectAll: () => void; onClearAll: () => void }) {
  return (
    <div className={css.bulkWrap}>
      <button className={css.bulkSelectBtn} onClick={onSelectAll}>Select All</button>
      <button className={css.bulkClearBtn} onClick={onClearAll}>Clear All</button>
    </div>
  );
}
