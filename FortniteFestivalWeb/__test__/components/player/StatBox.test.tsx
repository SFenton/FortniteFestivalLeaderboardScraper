import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import StatBox from '../../../src/components/player/StatBox';

describe('StatBox', () => {
  it('renders label and value', () => {
    render(<StatBox label="Songs Played" value="42" />);
    expect(screen.getByText('Songs Played')).toBeDefined();
    expect(screen.getByText('42')).toBeDefined();
  });

  it('applies custom color to value', () => {
    const { container } = render(<StatBox label="Accuracy" value="95%" color="rgb(46,204,113)" />);
    const value = container.querySelector('span');
    expect(value?.style.color).toBe('rgb(46, 204, 113)');
  });

  it('renders clickable wrapper with chevron when onClick provided', () => {
    const onClick = vi.fn();
    const { container } = render(<StatBox label="Songs" value="10" onClick={onClick} />);

    // Should have an SVG chevron
    expect(container.querySelector('svg')).toBeDefined();

    // Click should trigger the handler
    fireEvent.click(screen.getByRole('button', { name: /Songs/i }));
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('activates from touch pointerup and suppresses the follow-up click', () => {
    const onClick = vi.fn();
    render(<StatBox label="Songs" value="10" onClick={onClick} />);
    const card = screen.getByRole('button', { name: /Songs/i });

    fireEvent.pointerDown(card, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    fireEvent.pointerUp(card, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    expect(onClick).toHaveBeenCalledTimes(1);

    fireEvent.click(card);
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('cancels touch activation when the gesture turns into a scroll', () => {
    const onClick = vi.fn();
    render(<StatBox label="Songs" value="10" onClick={onClick} />);
    const card = screen.getByRole('button', { name: /Songs/i });

    fireEvent.pointerDown(card, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    expect(card.getAttribute('data-pressed')).toBe('true');
    fireEvent.pointerMove(card, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 28 });
    expect(card.getAttribute('data-pressed')).toBeNull();
    fireEvent.pointerUp(card, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 28 });

    expect(onClick).not.toHaveBeenCalled();
  });

  it('supports keyboard activation for clickable stat cards', () => {
    const onClick = vi.fn();
    render(<StatBox label="Songs" value="10" onClick={onClick} />);
    const card = screen.getByRole('button', { name: /Songs/i });

    fireEvent.keyDown(card, { key: 'Enter' });
    fireEvent.keyDown(card, { key: ' ' });

    expect(onClick).toHaveBeenCalledTimes(2);
  });

  it('does not make static stat cards focusable or pressable', () => {
    const { container } = render(<StatBox label="Score" value="1000" />);
    expect(screen.queryByRole('button')).toBeNull();
    expect(container.firstElementChild?.getAttribute('tabindex')).toBeNull();
  });

  it('uses a manipulation touch action for clickable stat cards', () => {
    render(<StatBox label="Songs" value="10" onClick={vi.fn()} />);
    expect(screen.getByRole('button', { name: /Songs/i }).style.touchAction).toBe('manipulation');
  });

  it('does not render chevron when onClick is not provided', () => {
    const { container } = render(<StatBox label="Score" value="1000" />);
    expect(container.querySelector('svg')).toBeNull();
  });

  it('renders ReactNode values', () => {
    render(<StatBox label="Stars" value={<span data-testid="stars">★★★</span>} />);
    expect(screen.getByTestId('stars')).toBeDefined();
  });
});
