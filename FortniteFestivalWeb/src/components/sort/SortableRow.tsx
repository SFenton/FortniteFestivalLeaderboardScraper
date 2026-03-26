/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { modalStyles as ms } from '../modals/modalStyles';
import type { ReorderItem } from './reorderTypes';

export interface SortableRowProps {
  item: ReorderItem;
}

export function SortableRow({ item }: SortableRowProps) {
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({ id: item.key });

  const style: React.CSSProperties = {
    ...ms.reorderRow,
    transform: CSS.Transform.toString(transform),
    transition,
    zIndex: isDragging ? 10 : undefined,
    opacity: isDragging ? 0.85 : 1,
    boxShadow: isDragging ? '0 4px 16px rgba(0,0,0,0.4)' : undefined,
    cursor: isDragging ? 'grabbing' : 'grab',
  };

  return (
    <div ref={setNodeRef} style={style} {...attributes} {...listeners}>
      <span style={ms.dragHandle}>
        <svg width="12" height="18" viewBox="0 0 12 18" fill="currentColor" aria-hidden="true">
          <circle cx="3" cy="3" r="1.5" /><circle cx="9" cy="3" r="1.5" />
          <circle cx="3" cy="9" r="1.5" /><circle cx="9" cy="9" r="1.5" />
          <circle cx="3" cy="15" r="1.5" /><circle cx="9" cy="15" r="1.5" />
        </svg>
      </span>
      <span style={ms.reorderLabel}>{item.label}</span>
    </div>
  );
}
