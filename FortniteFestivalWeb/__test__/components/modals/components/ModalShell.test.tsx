import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import { useState } from 'react';
import ModalShell from '../../../../src/components/modals/components/ModalShell';

// Mock useIsMobile
const mockIsMobile = vi.fn(() => false);
vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({ useIsMobile: () => mockIsMobile() }));

let mockVisualViewportHeight = 800;
let mockVisualViewportOffsetTop = 0;
vi.mock('../../../../src/hooks/ui/useVisualViewport', () => ({
  useVisualViewportHeight: () => mockVisualViewportHeight,
  useVisualViewportOffsetTop: () => mockVisualViewportOffsetTop,
}));

function dispatchPointer(target: Element, type: string, props: Partial<PointerEvent> = {}) {
  const event = new Event(type, { bubbles: true, cancelable: true }) as PointerEvent;
  Object.defineProperties(event, {
    pointerId: { value: props.pointerId ?? 1 },
    pointerType: { value: props.pointerType ?? 'touch' },
    isPrimary: { value: props.isPrimary ?? true },
    button: { value: props.button ?? 0 },
    clientX: { value: props.clientX ?? 0 },
    clientY: { value: props.clientY ?? 0 },
    timeStamp: { value: props.timeStamp ?? 0 },
  });
  fireEvent(target, event);
  return event;
}

function dispatchClick(target: Element, props: { clientX?: number; clientY?: number; timeStamp?: number } = {}) {
  const event = new MouseEvent('click', {
    bubbles: true,
    cancelable: true,
    clientX: props.clientX ?? 0,
    clientY: props.clientY ?? 0,
  });
  Object.defineProperty(event, 'timeStamp', { value: props.timeStamp ?? 0 });
  fireEvent(target, event);
  return event;
}

function ModalRetargetHarness({ onUnderlyingPress }: { onUnderlyingPress: () => void }) {
  const [visible, setVisible] = useState(true);
  return (
    <>
      <button type="button" onClick={onUnderlyingPress}>Underlying</button>
      <ModalShell visible={visible} title="Test" onClose={() => setVisible(false)}>
        <div>Content</div>
      </ModalShell>
    </>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mockIsMobile.mockReturnValue(false);
  mockVisualViewportHeight = 800;
  mockVisualViewportOffsetTop = 0;
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
});

afterEach(() => {
  vi.useRealTimers();
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

  it('allows overlay and panel hit-testing immediately before the entrance frame runs', () => {
    vi.mocked(window.requestAnimationFrame).mockImplementation(() => 1);
    render(
      <ModalShell visible={true} title="Test Modal" onClose={vi.fn()}>
        <div>Modal Content</div>
      </ModalShell>,
    );

    const dialog = screen.getByRole('dialog');
    const overlay = dialog.previousElementSibling as HTMLElement;
    expect(overlay.style.pointerEvents).toBe('auto');
    expect(dialog.style.pointerEvents).toBe('auto');
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
    render(
      <ModalShell visible={true} title="Test" onClose={onClose}>
        <div>Content</div>
      </ModalShell>,
    );
    // Overlay is the first sibling of the dialog (positioned fixed, inset 0)
    const dialog = screen.getByRole('dialog');
    const overlay = dialog.previousElementSibling;
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

  it('calls onClose from touch pointerup on the close button without double firing on click', () => {
    const onClose = vi.fn();
    render(
      <ModalShell visible={true} title="Test" onClose={onClose}>
        <div>Content</div>
      </ModalShell>,
    );

    const closeBtn = screen.getByRole('button', { name: /close/i });
    fireEvent.pointerDown(closeBtn, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 370, clientY: 120 });
    fireEvent.pointerUp(closeBtn, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 370, clientY: 120 });

    expect(onClose).toHaveBeenCalledTimes(1);

    fireEvent.click(closeBtn);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('suppresses a compatibility click retargeted beneath a closing modal', () => {
    const onUnderlyingPress = vi.fn();
    render(<ModalRetargetHarness onUnderlyingPress={onUnderlyingPress} />);
    const closeBtn = screen.getByRole('button', { name: /close/i });

    dispatchPointer(closeBtn, 'pointerdown', { clientX: 370, clientY: 120, timeStamp: 10 });
    dispatchPointer(closeBtn, 'pointerup', { clientX: 370, clientY: 120, timeStamp: 20 });

    const retargetedClick = dispatchClick(screen.getByRole('button', { name: 'Underlying' }), { clientX: 370, clientY: 120, timeStamp: 80 });

    expect(retargetedClick.defaultPrevented).toBe(true);
    expect(onUnderlyingPress).not.toHaveBeenCalled();
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
    // Mobile panel has left/right set to 0
    expect(dialog.style.left).toBe('0px');
    expect(dialog.style.right).toBe('0px');
  });

  it('keeps the mobile panel top stable while extending to the viewport bottom when visual viewport shrinks', () => {
    mockIsMobile.mockReturnValue(true);
    mockVisualViewportHeight = 844;
    mockVisualViewportOffsetTop = 0;
    const { rerender } = render(
      <ModalShell visible={true} title="Mobile Modal" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    const dialog = screen.getByRole('dialog');
    expect(dialog.style.top).toBe('168.8px');

    mockVisualViewportHeight = 520;
    rerender(
      <ModalShell visible={true} title="Mobile Modal" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );

    expect(dialog.style.top).toBe('168.8px');
    expect(dialog.style.bottom).toBe('0px');
    expect(dialog.style.height).toBe('');
  });

  it('renders desktop panel class when isMobile is false', () => {
    mockIsMobile.mockReturnValue(false);
    render(
      <ModalShell visible={true} title="Desktop Modal" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    const dialog = screen.getByRole('dialog');
    // Desktop panel has width 80vw
    expect(dialog.style.width).toBe('80vw');
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
    const dialog = screen.getByRole('dialog');
    const style = dialog.getAttribute('style') ?? '';
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

  it('unmounts after the close timeout fallback when transitionend does not fire', () => {
    vi.useFakeTimers();
    const onCloseComplete = vi.fn();
    const { rerender } = render(
      <ModalShell visible={true} title="Test" onClose={vi.fn()} onCloseComplete={onCloseComplete} transitionMs={300}>
        <div>Content</div>
      </ModalShell>,
    );

    rerender(
      <ModalShell visible={false} title="Test" onClose={vi.fn()} onCloseComplete={onCloseComplete} transitionMs={300}>
        <div>Content</div>
      </ModalShell>,
    );

    expect(screen.getByRole('dialog')).toBeTruthy();
    act(() => { vi.advanceTimersByTime(350); });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(onCloseComplete).toHaveBeenCalledTimes(1);
  });

  it('overlay has data-glow-scope to suppress light painting', () => {
    render(
      <ModalShell visible={true} title="Test" onClose={vi.fn()}>
        <div>Content</div>
      </ModalShell>,
    );
    const dialog = screen.getByRole('dialog');
    const overlay = dialog.previousElementSibling as HTMLElement;
    expect(overlay.hasAttribute('data-glow-scope')).toBe(true);
  });
});
