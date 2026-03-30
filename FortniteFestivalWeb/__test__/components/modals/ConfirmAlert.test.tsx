import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import ConfirmAlert from '../../../src/components/modals/ConfirmAlert';
import { TRANSITION_MS } from '@festival/theme';

type ConfirmAlertProps = { title: string; message: string; onNo: () => void; onYes: () => void; onExitComplete?: () => void };

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
    expect(screen.getByText('Yes')).toBeDefined();
    expect(screen.getByText('No')).toBeDefined();
  });

  it('calls onNo when No is clicked', () => {
    let called = false;
    render(<ConfirmAlert {...defaultProps} onNo={() => { called = true; }} />);
    fireEvent.click(screen.getByText('No'));
    expect(called).toBe(true);
  });

  it('calls onYes when Yes is clicked', () => {
    let called = false;
    render(<ConfirmAlert {...defaultProps} onYes={() => { called = true; }} />);
    fireEvent.click(screen.getByText('Yes'));
    expect(called).toBe(true);
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

  it('card click does not propagate to overlay', () => {
    const onNo = vi.fn();
    render(<ConfirmAlert {...defaultProps} onNo={onNo} />);
    // Portal renders to document.body; card is the first child inside the overlay
    const overlay = document.body.lastElementChild as HTMLElement;
    const card = overlay.firstElementChild as HTMLElement;
    fireEvent.click(card);
    expect(onNo).not.toHaveBeenCalled();
  });

  describe('exit animation', () => {
    it('clicking No with onExitComplete triggers exit animation instead of onNo', () => {
      vi.useFakeTimers();
      const onNo = vi.fn();
      const onExitComplete = vi.fn();
      render(<ConfirmAlert {...defaultProps} onNo={onNo} onExitComplete={onExitComplete} />);
      fireEvent.click(screen.getByText('No'));
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
      fireEvent.click(screen.getByText('Yes'));
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
      fireEvent.click(screen.getByText('No'));
      fireEvent.click(screen.getByText('No'));
      act(() => { vi.advanceTimersByTime(TRANSITION_MS); });
      expect(onExitComplete).toHaveBeenCalledTimes(1);
      vi.useRealTimers();
    });

    it('without onExitComplete, onNo is called directly (backward compat)', () => {
      const onNo = vi.fn();
      render(<ConfirmAlert {...defaultProps} onNo={onNo} />);
      fireEvent.click(screen.getByText('No'));
      expect(onNo).toHaveBeenCalled();
    });
  });

  it('overlay has data-glow-scope to suppress light painting', () => {
    render(<ConfirmAlert {...defaultProps} />);
    const overlay = document.body.lastElementChild as HTMLElement;
    expect(overlay.hasAttribute('data-glow-scope')).toBe(true);
  });
});
