import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import CardPressable from '../../../src/components/common/CardPressable';

describe('CardPressable', () => {
  it('activates from touch pointerup and suppresses the follow-up click', () => {
    const onPress = vi.fn();
    render(<CardPressable onPress={onPress}>Open card</CardPressable>);
    const card = screen.getByRole('button', { name: 'Open card' });

    fireEvent.pointerDown(card, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    fireEvent.pointerUp(card, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    expect(onPress).toHaveBeenCalledTimes(1);

    fireEvent.click(card);
    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('cancels activation when touch movement becomes a scroll', () => {
    const onPress = vi.fn();
    render(<CardPressable onPress={onPress}>Open card</CardPressable>);
    const card = screen.getByRole('button', { name: 'Open card' });

    fireEvent.pointerDown(card, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    expect(card.getAttribute('data-pressed')).toBe('true');

    fireEvent.pointerMove(card, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 28 });
    expect(card.getAttribute('data-pressed')).toBeNull();

    fireEvent.pointerUp(card, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 28 });
    expect(onPress).not.toHaveBeenCalled();
  });

  it('supports keyboard activation and manipulation touch action', () => {
    const onPress = vi.fn();
    render(<CardPressable onPress={onPress}>Open card</CardPressable>);
    const card = screen.getByRole('button', { name: 'Open card' });

    expect(card.style.touchAction).toBe('manipulation');
    expect(card.hasAttribute('data-card-pressable')).toBe(true);

    fireEvent.keyDown(card, { key: 'Enter' });
    fireEvent.keyDown(card, { key: ' ' });
    expect(onPress).toHaveBeenCalledTimes(2);
  });

  it('ignores nested interactive descendants', () => {
    const onPress = vi.fn();
    const nestedPress = vi.fn();
    render(
      <CardPressable onPress={onPress}>
        <button type="button" onClick={nestedPress}>Nested</button>
      </CardPressable>,
    );
    const nested = screen.getAllByRole('button', { name: 'Nested' })[1]!;

    fireEvent.pointerDown(nested, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    fireEvent.pointerUp(nested, { pointerId: 1, pointerType: 'touch', isPrimary: true, button: 0, clientX: 20, clientY: 20 });
    fireEvent.click(nested);

    expect(nestedPress).toHaveBeenCalledTimes(1);
    expect(onPress).not.toHaveBeenCalled();
  });
});