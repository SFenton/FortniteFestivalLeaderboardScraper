import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import ShopSortModal from '../../../../src/pages/shop/modals/ShopSortModal';
import type { ShopSortSettings } from '../../../../src/pages/shop/shopSort';
import { TestProviders } from '../../../helpers/TestProviders';

const baseDraft = (): ShopSortSettings => ({ sortMode: 'title', sortAscending: true });

const defaultProps = () => ({
  visible: true,
  draft: baseDraft(),
  savedDraft: baseDraft() as ShopSortSettings | undefined,
  onChange: vi.fn(),
  onCancel: vi.fn(),
  onReset: vi.fn(),
  onApply: vi.fn(),
});

function renderModal(overrides: Partial<ReturnType<typeof defaultProps>> = {}) {
  const props = { ...defaultProps(), ...overrides };
  return { ...render(<TestProviders><ShopSortModal {...props} /></TestProviders>), props };
}

describe('ShopSortModal', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('renders nothing when hidden', () => {
    const { container } = renderModal({ visible: false });
    expect(container.querySelector('[role="dialog"]')).toBeNull();
  });

  it('shows the title and exactly the Songs general sort modes', () => {
    renderModal();
    expect(screen.getByText('Sort Item Shop')).toBeDefined();
    for (const label of ['Title', 'Artist', 'Year', 'Duration']) {
      expect(screen.getByText(label)).toBeDefined();
    }
    expect(screen.queryByText('Item Shop')).toBeNull();
    expect(screen.queryByText('Has FC')).toBeNull();
  });

  it.each([
    ['Artist', 'artist'],
    ['Year', 'year'],
    ['Duration', 'duration'],
  ] as const)('selecting %s sets the sort mode', (label, mode) => {
    const { props } = renderModal();
    fireEvent.click(screen.getByText(label));
    expect(props.onChange).toHaveBeenCalledWith({ sortMode: mode, sortAscending: true });
  });

  it('toggles direction and keeps the mode', () => {
    const { props } = renderModal({ draft: { sortMode: 'year', sortAscending: true } });
    fireEvent.click(screen.getByLabelText('Descending'));
    expect(props.onChange).toHaveBeenCalledWith({ sortMode: 'year', sortAscending: false });
  });

  it('shows the direction hint for the current direction', () => {
    renderModal({ draft: { sortMode: 'title', sortAscending: false } });
    expect(screen.getByText('Descending (Z–A, high–low)')).toBeDefined();
  });

  it('disables Apply until the draft changes', () => {
    renderModal();
    expect(screen.getByText('Apply Sort Changes').closest('button')!.disabled).toBe(true);
  });

  it('applies a changed draft', () => {
    const { props } = renderModal({ draft: { sortMode: 'duration', sortAscending: false } });
    const btn = screen.getByText('Apply Sort Changes').closest('button')!;
    expect(btn.disabled).toBe(false);
    fireEvent.click(btn);
    expect(props.onApply).toHaveBeenCalledTimes(1);
  });

  it('calls onReset from the Reset button', () => {
    const { props } = renderModal();
    const resetBtns = screen.getAllByRole('button', { name: 'Reset' });
    fireEvent.click(resetBtns[resetBtns.length - 1]!);
    expect(props.onReset).toHaveBeenCalledTimes(1);
  });

  it('closes directly when there are no changes', () => {
    const { props } = renderModal();
    fireEvent.click(screen.getByLabelText('Close'));
    expect(props.onCancel).toHaveBeenCalledTimes(1);
  });

  it('asks before discarding changes', () => {
    const { props } = renderModal({ draft: { sortMode: 'artist', sortAscending: true } });
    fireEvent.click(screen.getByLabelText('Close'));
    expect(props.onCancel).not.toHaveBeenCalled();
    expect(screen.getByText('Discard Sort Changes')).toBeDefined();
  });

  describe('accessibility', () => {
    it('names the dialog and exposes every control by role and name in reading order', () => {
      renderModal();
      const dialog = screen.getByRole('dialog', { name: 'Sort Item Shop' });
      const names = within(dialog).getAllByRole('button').map(button => button.getAttribute('aria-label') ?? button.textContent?.trim());
      expect(names).toEqual(['Close', 'Title', 'Artist', 'Year', 'Duration', 'Ascending', 'Descending', 'Reset', 'Apply Sort Changes']);
      expect(within(dialog).getByRole('heading', { name: 'Sort Item Shop' })).toBeDefined();
    });

    it('marks exactly the current mode and direction as pressed', () => {
      renderModal({ draft: { sortMode: 'duration', sortAscending: false } });
      const dialog = screen.getByRole('dialog', { name: 'Sort Item Shop' });
      for (const mode of ['Title', 'Artist', 'Year']) {
        expect(within(dialog).getByRole('button', { name: mode, pressed: false })).toBeDefined();
      }
      expect(within(dialog).getByRole('button', { name: 'Duration', pressed: true })).toBeDefined();
      expect(within(dialog).getByRole('button', { name: 'Ascending', pressed: false })).toBeDefined();
      expect(within(dialog).getByRole('button', { name: 'Descending', pressed: true })).toBeDefined();
    });

    it('exposes the disabled Apply state until the draft changes', () => {
      renderModal();
      expect((screen.getByRole('button', { name: 'Apply Sort Changes' }) as HTMLButtonElement).disabled).toBe(true);
    });
  });
});
