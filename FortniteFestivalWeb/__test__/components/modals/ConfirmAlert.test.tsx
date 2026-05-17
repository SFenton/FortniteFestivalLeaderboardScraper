import { describe, it, expect, vi, beforeAll } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import ConfirmAlert from '../../../src/components/modals/ConfirmAlert';
import { Gap, Layout, TRANSITION_MS } from '@festival/theme';

beforeAll(() => {
  if (typeof Range !== 'undefined') {
    Object.defineProperty(Range.prototype, 'getBoundingClientRect', {
      configurable: true,
      value: () => ({ top: 0, right: 120, bottom: 16, left: 0, width: 120, height: 16, x: 0, y: 0, toJSON() { return this; } }),
    });
    Object.defineProperty(Range.prototype, 'getClientRects', {
      configurable: true,
      value: () => [] as unknown as DOMRectList,
    });
  }
});

type ConfirmAlertProps = {
  title: string;
  message: string;
  onNo: () => void;
  onYes: () => void;
  onExitComplete?: () => void;
  noLabel?: string;
  yesLabel?: string;
};

describe('ConfirmAlert', () => {
  const defaultProps: ConfirmAlertProps = {
    title: 'Confirm Action',
    message: 'Are you sure?',
    onNo: () => {},
    onYes: () => {},
  };

  it('renders title and message', () => {
    render(<ConfirmAlert {...defaultProps} />);
    expect(screen.getByText('Confirm Action')).toBeDefined();
    expect(screen.getByText('Are you sure?')).toBeDefined();
  });

  it('renders Yes and No buttons', () => {
    render(<ConfirmAlert {...defaultProps} />);
    expect(screen.getByRole('button', { name: 'Yes' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'No' })).toBeDefined();
  });

  it('uses compact sizing, stretch alignment, and marquee button labels', () => {
    render(
      <ConfirmAlert
        {...defaultProps}
        noLabel="Okay, Keep It Open"
        yesLabel="Permanently Dismiss"
      />,
    );

    const overlay = document.body.lastElementChild as HTMLElement;
    const card = overlay.firstElementChild as HTMLElement;
    const noButton = screen.getByRole('button', { name: 'Okay, Keep It Open' }) as HTMLButtonElement;
    const yesButton = screen.getByRole('button', { name: 'Permanently Dismiss' }) as HTMLButtonElement;
    const buttonsContainer = noButton.parentElement as HTMLElement;
    const noMarquee = noButton.querySelector('div') as HTMLElement;
    const yesMarquee = yesButton.querySelector('div') as HTMLElement;

    expect(card.style.minWidth).toBe(`min(${Layout.confirmMinWidth}px, calc(100vw - ${Gap.section * 2}px))`);
    expect(card.style.width).toBe(`${Layout.confirmMaxWidth}px`);
    expect(card.style.maxWidth).toBe(`calc(100vw - ${Gap.section * 2}px)`);
    expect(card.style.boxSizing).toBe('border-box');
    expect(card.style.padding).toContain('safe-area-inset-bottom');
    expect(buttonsContainer.style.alignItems).toBe('stretch');
    expect(noButton.style.minWidth).toBe('0px');
    expect(yesButton.style.minWidth).toBe('0px');
    expect(noButton.style.whiteSpace).toBe('normal');
    expect(yesButton.style.whiteSpace).toBe('normal');
    expect(noButton.style.overflowWrap).toBe('anywhere');
    expect(yesButton.style.overflowWrap).toBe('anywhere');
    expect(noMarquee.style.width).toBe('100%');
    expect(yesMarquee.style.width).toBe('100%');
    expect(noMarquee.style.minWidth).toBe('0px');
    expect(yesMarquee.style.minWidth).toBe('0px');
  });

  it('calls onNo when No is clicked', () => {
    let called = false;
    render(<ConfirmAlert {...defaultProps} onNo={() => { called = true; }} />);
    fireEvent.click(screen.getByRole('button', { name: 'No' }));
    expect(called).toBe(true);
  });

  it('calls onYes when Yes is clicked', () => {
    let called = false;
    render(<ConfirmAlert {...defaultProps} onYes={() => { called = true; }} />);
    fireEvent.click(screen.getByRole('button', { name: 'Yes' }));
    expect(called).toBe(true);
  });

  it('commits button actions from touch pointerup without double firing on click', () => {
    const onYes = vi.fn();
    render(<ConfirmAlert {...defaultProps} onYes={onYes} />);
    const yesButton = screen.getByRole('button', { name: 'Yes' });

    fireEvent.pointerDown(yesButton, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 220, clientY: 420 });
    fireEvent.pointerUp(yesButton, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 220, clientY: 421 });

    expect(onYes).toHaveBeenCalledTimes(1);

    fireEvent.click(yesButton);
    expect(onYes).toHaveBeenCalledTimes(1);
  });

  it('calls onNo when Escape is pressed', () => {
    let called = false;
    render(<ConfirmAlert {...defaultProps} onNo={() => { called = true; }} />);
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(called).toBe(true);
  });

  it('pressing a non-Escape key does not call onNo', () => {
    const onNo = vi.fn();
    render(<ConfirmAlert {...defaultProps} onNo={onNo} />);
    fireEvent.keyDown(document, { key: 'Enter' });
    expect(onNo).not.toHaveBeenCalled();
  });

  it('overlay click calls onNo', () => {
    const onNo = vi.fn();
    render(<ConfirmAlert {...defaultProps} onNo={onNo} />);
    const overlay = document.body.lastElementChild as HTMLElement;
    fireEvent.click(overlay);
    expect(onNo).toHaveBeenCalled();
  });

  it('dismisses from touch pointerup on the overlay without double firing on click', () => {
    const onNo = vi.fn();
    render(<ConfirmAlert {...defaultProps} onNo={onNo} />);
    const overlay = document.body.lastElementChild as HTMLElement;

    fireEvent.pointerDown(overlay, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 40, clientY: 60 });
    fireEvent.pointerUp(overlay, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 40, clientY: 60 });

    expect(onNo).toHaveBeenCalledTimes(1);

    fireEvent.click(overlay);
    expect(onNo).toHaveBeenCalledTimes(1);
  });

  it('card click does not propagate to overlay', () => {
    const onNo = vi.fn();
    render(<ConfirmAlert {...defaultProps} onNo={onNo} />);
    // Portal renders to document.body; card is the first child inside the overlay
    const overlay = document.body.lastElementChild as HTMLElement;
    const card = overlay.firstElementChild as HTMLElement;
    fireEvent.click(card);
    expect(onNo).not.toHaveBeenCalled();
  });

  it('card touch pointerup does not propagate to the overlay', () => {
    const onNo = vi.fn();
    render(<ConfirmAlert {...defaultProps} onNo={onNo} />);
    const overlay = document.body.lastElementChild as HTMLElement;
    const card = overlay.firstElementChild as HTMLElement;

    fireEvent.pointerDown(card, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 220, clientY: 360 });
    fireEvent.pointerUp(card, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 220, clientY: 360 });

    expect(onNo).not.toHaveBeenCalled();
  });

  describe('exit animation', () => {
    it('clicking No with onExitComplete triggers exit animation instead of onNo', () => {
      vi.useFakeTimers();
      const onNo = vi.fn();
      const onExitComplete = vi.fn();
      render(<ConfirmAlert {...defaultProps} onNo={onNo} onExitComplete={onExitComplete} />);
      fireEvent.click(screen.getByRole('button', { name: 'No' }));
      expect(onNo).not.toHaveBeenCalled();
      expect(onExitComplete).not.toHaveBeenCalled();
      act(() => { vi.advanceTimersByTime(TRANSITION_MS); });
      expect(onExitComplete).toHaveBeenCalled();
      vi.useRealTimers();
    });

    it('clicking Yes with onExitComplete fires onYes immediately then onExitComplete after delay', () => {
      vi.useFakeTimers();
      const onYes = vi.fn();
      const onExitComplete = vi.fn();
      render(<ConfirmAlert {...defaultProps} onYes={onYes} onExitComplete={onExitComplete} />);
      fireEvent.click(screen.getByRole('button', { name: 'Yes' }));
      expect(onYes).toHaveBeenCalled();
      expect(onExitComplete).not.toHaveBeenCalled();
      act(() => { vi.advanceTimersByTime(TRANSITION_MS); });
      expect(onExitComplete).toHaveBeenCalled();
      vi.useRealTimers();
    });

    it('Escape with onExitComplete triggers exit animation instead of onNo', () => {
      vi.useFakeTimers();
      const onNo = vi.fn();
      const onExitComplete = vi.fn();
      render(<ConfirmAlert {...defaultProps} onNo={onNo} onExitComplete={onExitComplete} />);
      fireEvent.keyDown(document, { key: 'Escape' });
      expect(onNo).not.toHaveBeenCalled();
      act(() => { vi.advanceTimersByTime(TRANSITION_MS); });
      expect(onExitComplete).toHaveBeenCalled();
      vi.useRealTimers();
    });

    it('overlay click with onExitComplete triggers exit animation instead of onNo', () => {
      vi.useFakeTimers();
      const onNo = vi.fn();
      const onExitComplete = vi.fn();
      render(<ConfirmAlert {...defaultProps} onNo={onNo} onExitComplete={onExitComplete} />);
      const overlay = document.body.lastElementChild as HTMLElement;
      fireEvent.click(overlay);
      expect(onNo).not.toHaveBeenCalled();
      act(() => { vi.advanceTimersByTime(TRANSITION_MS); });
      expect(onExitComplete).toHaveBeenCalled();
      vi.useRealTimers();
    });

    it('double-click during exit animation is ignored', () => {
      vi.useFakeTimers();
      const onExitComplete = vi.fn();
      render(<ConfirmAlert {...defaultProps} onExitComplete={onExitComplete} />);
      fireEvent.click(screen.getByRole('button', { name: 'No' }));
      fireEvent.click(screen.getByRole('button', { name: 'No' }));
      act(() => { vi.advanceTimersByTime(TRANSITION_MS); });
      expect(onExitComplete).toHaveBeenCalledTimes(1);
      vi.useRealTimers();
    });

    it('without onExitComplete, onNo is called directly (backward compat)', () => {
      const onNo = vi.fn();
      render(<ConfirmAlert {...defaultProps} onNo={onNo} />);
      fireEvent.click(screen.getByRole('button', { name: 'No' }));
      expect(onNo).toHaveBeenCalled();
    });
  });

  it('overlay has data-glow-scope to suppress light painting', () => {
    render(<ConfirmAlert {...defaultProps} />);
    const overlay = document.body.lastElementChild as HTMLElement;
    expect(overlay.hasAttribute('data-glow-scope')).toBe(true);
  });
});
