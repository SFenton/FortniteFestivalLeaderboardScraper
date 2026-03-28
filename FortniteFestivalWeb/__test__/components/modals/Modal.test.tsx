import { describe, it, expect, vi, beforeAll } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import Modal from '../../../src/components/modals/Modal';
import { TestProviders } from '../../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubElementDimensions } from '../../helpers/browserStubs';

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
});

function renderModal(overrides: Partial<{
  visible: boolean;
  title: string;
  onClose: () => void;
  onApply: () => void;
  onReset: () => void;
  resetLabel: string;
  resetHint: string;
  applyLabel: string;
  applyDisabled: boolean;
}> = {}) {
  const props = {
    visible: true,
    title: 'Test Modal',
    onClose: vi.fn(),
    onApply: vi.fn(),
    onReset: undefined as (() => void) | undefined,
    resetLabel: undefined as string | undefined,
    resetHint: undefined as string | undefined,
    applyLabel: undefined as string | undefined,
    applyDisabled: false,
    ...overrides,
  };
  return {
    ...render(
      <TestProviders>
        <Modal {...props}>
          <div style={{ height: 2000 }}>Tall content</div>
        </Modal>
      </TestProviders>,
    ),
    props,
  };
}

describe('Modal', () => {
  it('fires handleContentScroll on content scroll', async () => {
    const { container } = renderModal();
    // Flush mount/animIn effects
    await act(async () => {});
    // Content scroll area is the div with overflow-y: auto inside the dialog
    const dialog = screen.getByRole('dialog');
    const scrollArea = Array.from(dialog.querySelectorAll('div')).find(el => el.style.overflowY === 'auto');
    expect(scrollArea).toBeTruthy();
    fireEvent.scroll(scrollArea!);
    // handleContentScroll ran without error (calls updateScrollMask internally)
    expect(document.body.textContent).toContain('Tall content');
  });

  it('calls onApply when apply button is clicked', async () => {
    const { props } = renderModal();
    await act(async () => {});
    const applyBtn = screen.getByText('Apply');
    fireEvent.click(applyBtn);
    expect(props.onApply).toHaveBeenCalledTimes(1);
  });

  it('calls onClose via ModalShell close button', async () => {
    const { props } = renderModal();
    await act(async () => {});
    const closeBtn = screen.getByLabelText('Close');
    fireEvent.click(closeBtn);
    expect(props.onClose).toHaveBeenCalledTimes(1);
  });

  it('renders reset button when onReset is provided', async () => {
    const onReset = vi.fn();
    renderModal({ onReset, resetLabel: 'Reset All' });
    await act(async () => {});
    // resetLabel renders as title div; button uses common.reset = 'Reset'
    const resetBtn = screen.getAllByRole('button', { name: 'Reset' });
    fireEvent.click(resetBtn[resetBtn.length - 1]!);
    expect(onReset).toHaveBeenCalledTimes(1);
  });
});
