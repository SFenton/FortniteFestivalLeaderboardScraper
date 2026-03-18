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
  verticalListSortingStrategy,
  arrayMove,
} from '@dnd-kit/sortable';
import { SortableRow } from './SortableRow';
import css from '../modals/Modal.module.css';
import type { ReorderItem } from './reorderTypes';
export type { ReorderItem };

export interface ReorderListProps {
  items: ReorderItem[];
  onReorder: (items: ReorderItem[]) => void;
}

export function ReorderList({ items, onReorder }: ReorderListProps) {
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 150, tolerance: 5 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  /* v8 ignore start — DnD handler */
  const handleDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const oldIndex = items.findIndex(i => i.key === active.id);
    const newIndex = items.findIndex(i => i.key === over.id);
    if (oldIndex === -1 || newIndex === -1) return;
    onReorder(arrayMove(items, oldIndex, newIndex));
    /* v8 ignore stop */
  };

  return (
    <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
      <SortableContext items={items.map(i => i.key)} strategy={verticalListSortingStrategy}>
        <div className={css.reorderList}>
          {items.map((item) => (
            <SortableRow key={item.key} item={item} />
          ))}
        </div>
      </SortableContext>
    </DndContext>
  );
}
