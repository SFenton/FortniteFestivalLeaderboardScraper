import { describe, it, expect, vi } from 'vitest';
import { render } from '@testing-library/react';

vi.mock('@dnd-kit/sortable', async () => {
  const actual = await vi.importActual<typeof import('@dnd-kit/sortable')>('@dnd-kit/sortable');
  let mockIsDragging = false;
  return {
    ...actual,
    useSortable: () => ({
      attributes: {},
      listeners: {},
      setNodeRef: () => {},
      transform: null,
      transition: undefined,
      isDragging: mockIsDragging,
    }),
    __setMockIsDragging: (v: boolean) => { mockIsDragging = v; },
  };
});

import { SortableRow } from '../../components/sort/SortableRow';

describe('SortableRow', () => {
  it('renders with isDragging = false (default)', () => {
    const { container } = render(
      <SortableRow item={{ key: 'a', label: 'Alpha' }} />,
    );
    const row = container.firstElementChild as HTMLElement;
    expect(row.style.opacity).toBe('1');
    expect(row.style.cursor).toBe('grab');
  });

  it('renders with isDragging = true', async () => {
    const mod = await import('@dnd-kit/sortable') as any;
    mod.__setMockIsDragging(true);
    const { container } = render(
      <SortableRow item={{ key: 'b', label: 'Beta' }} />,
    );
    const row = container.firstElementChild as HTMLElement;
    expect(row.style.opacity).toBe('0.85');
    expect(row.style.cursor).toBe('grabbing');
    expect(row.style.zIndex).toBe('10');
    mod.__setMockIsDragging(false);
  });
});
