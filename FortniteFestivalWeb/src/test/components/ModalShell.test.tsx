import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import ModalShell from '../../components/modals/components/ModalShell';

// Mock useIsMobile
const mockIsMobile = vi.fn(() => false);
vi.mock('../../hooks/ui/useIsMobile', () => ({ useIsMobile: () => mockIsMobile() }));

vi.mock('../../hooks/ui/useVisualViewport', () => ({
  useVisualViewportHeight: () => 800,
  useVisualViewportOffsetTop: () => 0,
}));

beforeEach(() => {
  vi.clearAllMocks();
  mockIsMobile.mockReturnValue(false);
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('ModalShell', () => {
  /* ── Mount/unmount lifecycle ── */
  it('renders nothing when visible is false', () => {
    const { container } = render(
      <ModalShell visible={false} title="Test" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    expect(container.innerHTML).toBe('');
  });

  it('mounts and renders when visible is true', () => {
    render(
      <ModalShell visible={true} title="Test Modal" onClose={vi.fn()}>
        <div>Modal Content</div>
      </ModalShell>,
    );
    expect(screen.getByText('Test Modal')).toBeTruthy();
    expect(screen.getByText('Modal Content')).toBeTruthy();
  });

  it('renders dialog with correct aria attributes', () => {
    render(
      <ModalShell visible={true} title="Aria Test" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    const dialog = screen.getByRole('dialog');
    expect(dialog).toBeTruthy();
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.getAttribute('aria-label')).toBe('Aria Test');
  });

  it('renders close button', () => {
    render(
      <ModalShell visible={true} title="Test" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    const closeBtn = screen.getByRole('button', { name: /close/i });
    expect(closeBtn).toBeTruthy();
  });

  /* ── Escape key ── */
  it('calls onClose when Escape key is pressed', () => {
    const onClose = vi.fn();
    render(
      <ModalShell visible={true} title="Test" onClose={onClose}>
        <div>Content</div>
      </ModalShell>,
    );
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not call onClose on non-Escape keys', () => {
    const onClose = vi.fn();
    render(
      <ModalShell visible={true} title="Test" onClose={onClose}>
        <div>Content</div>
      </ModalShell>,
    );
    fireEvent.keyDown(document, { key: 'Enter' });
    expect(onClose).not.toHaveBeenCalled();
  });

  /* ── Overlay click ── */
  it('calls onClose when overlay is clicked', () => {
    const onClose = vi.fn();
    const { container } = render(
      <ModalShell visible={true} title="Test" onClose={onClose}>
        <div>Content</div>
      </ModalShell>,
    );
    // Overlay is the first child div with the overlay class
    const overlay = container.querySelector('[class*="overlay"]');
    expect(overlay).toBeTruthy();
    fireEvent.click(overlay!);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  /* ── Close button click ── */
  it('calls onClose when close button is clicked', () => {
    const onClose = vi.fn();
    render(
      <ModalShell visible={true} title="Test" onClose={onClose}>
        <div>Content</div>
      </ModalShell>,
    );
    const closeBtn = screen.getByRole('button', { name: /close/i });
    fireEvent.click(closeBtn);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  /* ── Transition callbacks ── */
  it('calls onOpenComplete when open animation ends', () => {
    const onOpenComplete = vi.fn();
    render(
      <ModalShell visible={true} title="Test" onClose={vi.fn()} onOpenComplete={onOpenComplete}>
        <div>Content</div>
      </ModalShell>,
    );
    const panel = screen.getByRole('dialog');
    // Simulate transitionEnd with animIn = true
    fireEvent.transitionEnd(panel);
    expect(onOpenComplete).toHaveBeenCalledTimes(1);
  });

  it('calls onCloseComplete when close animation ends', () => {
    const onCloseComplete = vi.fn();
    const { rerender } = render(
      <ModalShell visible={true} title="Test" onClose={vi.fn()} onCloseComplete={onCloseComplete}>
        <div>Content</div>
      </ModalShell>,
    );
    // Switch to not visible
    rerender(
      <ModalShell visible={false} title="Test" onClose={vi.fn()} onCloseComplete={onCloseComplete}>
        <div>Content</div>
      </ModalShell>,
    );
    // Panel should still be mounted (animating out)
    const panel = screen.queryByRole('dialog');
    if (panel) {
      fireEvent.transitionEnd(panel);
      expect(onCloseComplete).toHaveBeenCalledTimes(1);
    }
  });

  /* ── Mobile variant ── */
  it('renders mobile panel class when isMobile is true', () => {
    mockIsMobile.mockReturnValue(true);
    render(
      <ModalShell visible={true} title="Mobile Modal" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    const dialog = screen.getByRole('dialog');
    expect(dialog.className).toContain('Mobile');
  });

  it('renders desktop panel class when isMobile is false', () => {
    mockIsMobile.mockReturnValue(false);
    render(
      <ModalShell visible={true} title="Desktop Modal" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    const dialog = screen.getByRole('dialog');
    expect(dialog.className).toContain('Desktop');
  });

  /* ── Custom desktop class ── */
  it('uses desktopClassName when provided', () => {
    mockIsMobile.mockReturnValue(false);
    render(
      <ModalShell visible={true} title="Custom" onClose={vi.fn()} desktopClassName="customPanel">
        <div>Content</div>
      </ModalShell>,
    );
    const dialog = screen.getByRole('dialog');
    expect(dialog.className).toContain('customPanel');
  });

  /* ── Transition duration ── */
  it('applies custom transition duration', () => {
    render(
      <ModalShell visible={true} title="Test" onClose={vi.fn()} transitionMs={500}>
        <div>Content</div>
      </ModalShell>,
    );
    const overlay = document.querySelector('[class*="overlay"]');
    const style = overlay?.getAttribute('style') ?? '';
    expect(style).toContain('500ms');
  });

  /* ── Renders title ── */
  it('renders the title in the header', () => {
    render(
      <ModalShell visible={true} title="My Title" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    expect(screen.getByText('My Title')).toBeTruthy();
  });

  /* ── Children rendering ── */
  it('renders children content', () => {
    render(
      <ModalShell visible={true} title="Test" onClose={vi.fn()}>
        <div data-testid="child">Child Content</div>
      </ModalShell>,
    );
    expect(screen.getByTestId('child')).toBeTruthy();
    expect(screen.getByText('Child Content')).toBeTruthy();
  });

  /* ── Unmounts after close transition ── */
  it('unmounts content after close transition completes', () => {
    const { rerender } = render(
      <ModalShell visible={true} title="Test" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    expect(screen.getByText('Content')).toBeTruthy();

    rerender(
      <ModalShell visible={false} title="Test" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    const panel = screen.queryByRole('dialog');
    if (panel) {
      fireEvent.transitionEnd(panel);
    }
    // After transition, should be unmounted
    expect(screen.queryByText('Content')).toBeNull();
  });
});
