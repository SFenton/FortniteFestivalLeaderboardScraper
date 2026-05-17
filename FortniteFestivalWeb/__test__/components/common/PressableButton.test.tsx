import { fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import PressableButton from '../../../src/components/common/PressableButton';

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

function RetargetHarness({ onUnderlyingPress }: { onUnderlyingPress: () => void }) {
  const [modalOpen, setModalOpen] = useState(true);
  return (
    <>
      <button type="button" onClick={onUnderlyingPress}>Underlying</button>
      {modalOpen && <PressableButton onPress={() => setModalOpen(false)}>Close modal</PressableButton>}
    </>
  );
}

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

  it('suppresses a compatibility click retargeted under an unmounted press target', () => {
    const onUnderlyingPress = vi.fn();
    render(<RetargetHarness onUnderlyingPress={onUnderlyingPress} />);
    const closeButton = screen.getByRole('button', { name: 'Close modal' });

    dispatchPointer(closeButton, 'pointerdown', { clientX: 20, clientY: 20, timeStamp: 10 });
    dispatchPointer(closeButton, 'pointerup', { clientX: 20, clientY: 20, timeStamp: 20 });

    expect(screen.queryByRole('button', { name: 'Close modal' })).toBeNull();
    const syntheticClick = dispatchClick(screen.getByRole('button', { name: 'Underlying' }), { clientX: 20, clientY: 20, timeStamp: 80 });

    expect(syntheticClick.defaultPrevented).toBe(true);
    expect(onUnderlyingPress).not.toHaveBeenCalled();
  });

  it('allows a later real click after the compatibility-click suppression window', () => {
    const onUnderlyingPress = vi.fn();
    render(<RetargetHarness onUnderlyingPress={onUnderlyingPress} />);
    const closeButton = screen.getByRole('button', { name: 'Close modal' });

    dispatchPointer(closeButton, 'pointerdown', { clientX: 20, clientY: 20, timeStamp: 10 });
    dispatchPointer(closeButton, 'pointerup', { clientX: 20, clientY: 20, timeStamp: 20 });
    dispatchClick(screen.getByRole('button', { name: 'Underlying' }), { clientX: 20, clientY: 20, timeStamp: 900 });

    expect(onUnderlyingPress).toHaveBeenCalledTimes(1);
  });

  it('allows an immediate click outside the compatibility-click suppression radius', () => {
    const onUnderlyingPress = vi.fn();
    render(<RetargetHarness onUnderlyingPress={onUnderlyingPress} />);
    const closeButton = screen.getByRole('button', { name: 'Close modal' });

    dispatchPointer(closeButton, 'pointerdown', { clientX: 20, clientY: 20, timeStamp: 10 });
    dispatchPointer(closeButton, 'pointerup', { clientX: 20, clientY: 20, timeStamp: 20 });
    const realClick = dispatchClick(screen.getByRole('button', { name: 'Underlying' }), { clientX: 80, clientY: 20, timeStamp: 80 });

    expect(realClick.defaultPrevented).toBe(false);
    expect(onUnderlyingPress).toHaveBeenCalledTimes(1);
  });
});
