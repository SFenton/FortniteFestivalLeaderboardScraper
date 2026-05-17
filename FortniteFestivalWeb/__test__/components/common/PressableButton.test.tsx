import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import PressableButton from '../../../src/components/common/PressableButton';

describe('PressableButton', () => {
  it('uses click as a fallback activation path', () => {
    const onPress = vi.fn();
    render(<PressableButton onPress={onPress}>Open</PressableButton>);

    fireEvent.click(screen.getByRole('button', { name: 'Open' }));

    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('activates on touch pointerup and suppresses the follow-up click', () => {
    const onPress = vi.fn();
    render(<PressableButton onPress={onPress}>Open</PressableButton>);
    const button = screen.getByRole('button', { name: 'Open' });

    fireEvent.pointerDown(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 10, clientY: 10 });
    fireEvent.click(button);

    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('cancels touch activation after movement', () => {
    const onPress = vi.fn();
    render(<PressableButton onPress={onPress}>Open</PressableButton>);
    const button = screen.getByRole('button', { name: 'Open' });

    fireEvent.pointerDown(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 25, clientY: 10 });

    expect(onPress).not.toHaveBeenCalled();
  });

  it('does not activate while disabled', () => {
    const onPress = vi.fn();
    render(<PressableButton onPress={onPress} disabled>Open</PressableButton>);

    fireEvent.click(screen.getByRole('button', { name: 'Open' }));

    expect(onPress).not.toHaveBeenCalled();
  });
});