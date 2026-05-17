import { describe, it, expect, vi } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import { useState } from 'react';
import { useCardPressAction, usePressAction } from '../../../src/hooks/ui/usePressAction';

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

function dispatchClick(target: Element, timeStampOrProps: number | { clientX?: number; clientY?: number; timeStamp?: number } = 0) {
  const props = typeof timeStampOrProps === 'number' ? { timeStamp: timeStampOrProps } : timeStampOrProps;
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

function mockRect(element: Element, rect: Partial<DOMRect> = {}) {
  vi.spyOn(element, 'getBoundingClientRect').mockReturnValue({
    x: rect.x ?? rect.left ?? 10,
    y: rect.y ?? rect.top ?? 20,
    left: rect.left ?? 10,
    top: rect.top ?? 20,
    right: rect.right ?? 210,
    bottom: rect.bottom ?? 120,
    width: rect.width ?? 200,
    height: rect.height ?? 100,
    toJSON: () => ({}),
  } as DOMRect);
}

function CardHarness({ onPress, disabled = false, withNested = false }: { onPress: () => void; disabled?: boolean; withNested?: boolean }) {
  const cardPress = useCardPressAction<HTMLDivElement>({ onPress, disabled });
  return (
    <div data-testid="card" role="button" tabIndex={0} data-pressed={cardPress.isPressed ? 'true' : 'false'} {...cardPress.pressHandlers}>
      Card
      {withNested && <button data-testid="nested" type="button">Nested</button>}
      {withNested && <span data-testid="stop" data-press-stop>Stop</span>}
    </div>
  );
}

function ControlHarness({ onParentPress, onPress }: { onParentPress: () => void; onPress: () => void }) {
  const pressHandlers = usePressAction<HTMLSpanElement>({ onPress, stopPropagation: true });
  return (
    <button type="button" data-testid="parent" onClick={onParentPress}>
      <span role="button" tabIndex={0} data-testid="control" {...pressHandlers}>Info</span>
    </button>
  );
}

function CardRetargetHarness({ onUnderlyingPress }: { onUnderlyingPress: () => void }) {
  const [cardOpen, setCardOpen] = useState(true);
  return (
    <>
      <button type="button" data-testid="underlying" onClick={onUnderlyingPress}>Underlying</button>
      {cardOpen && <CardHarness onPress={() => setCardOpen(false)} />}
    </>
  );
}

describe('useCardPressAction', () => {
  it('fires once on touch pointerup and suppresses the synthetic click', () => {
    const onPress = vi.fn();
    render(<CardHarness onPress={onPress} />);
    const card = screen.getByTestId('card');

    dispatchPointer(card, 'pointerdown', { clientX: 10, clientY: 10, timeStamp: 10 });
    dispatchPointer(card, 'pointerup', { clientX: 12, clientY: 12, timeStamp: 30 });
    expect(onPress).toHaveBeenCalledTimes(1);

    dispatchClick(card, 80);
    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('cancels on vertical movement without preventing pointerdown default', () => {
    const onPress = vi.fn();
    render(<CardHarness onPress={onPress} />);
    const card = screen.getByTestId('card');

    const pointerDown = dispatchPointer(card, 'pointerdown', { clientX: 10, clientY: 10, timeStamp: 10 });
    expect(pointerDown.defaultPrevented).toBe(false);
    expect(card.getAttribute('data-pressed')).toBe('true');

    dispatchPointer(card, 'pointermove', { clientX: 10, clientY: 16, timeStamp: 20 });
    expect(card.getAttribute('data-pressed')).toBe('false');

    dispatchPointer(card, 'pointerup', { clientX: 10, clientY: 16, timeStamp: 30 });
    expect(onPress).not.toHaveBeenCalled();
  });

  it('adds neutral press pulse metadata and lets it finish after release', () => {
    vi.useFakeTimers();
    try {
      const onPress = vi.fn();
      render(<CardHarness onPress={onPress} />);
      const card = screen.getByTestId('card') as HTMLElement;
      mockRect(card);

      dispatchPointer(card, 'pointerdown', { clientX: 60, clientY: 70, timeStamp: 10 });
      expect(card).toHaveAttribute('data-press-pulse', 'true');
      expect(card.style.getPropertyValue('--press-pulse-x')).toBe('50px');
      expect(card.style.getPropertyValue('--press-pulse-y')).toBe('50px');
      expect(card.style.getPropertyValue('--press-pulse-size')).toBe('360px');
      expect(card.style.getPropertyValue('--press-pulse-mid-size')).toBe('187px');

      dispatchPointer(card, 'pointerup', { clientX: 60, clientY: 70, timeStamp: 30 });
      expect(card).toHaveAttribute('data-press-pulse', 'true');

      act(() => { vi.advanceTimersByTime(1000); });
      expect(card).not.toHaveAttribute('data-press-pulse');
      expect(card.style.getPropertyValue('--press-pulse-x')).toBe('');
      expect(card.style.getPropertyValue('--press-pulse-mid-size')).toBe('');
      expect(onPress).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('clears the press pulse when movement becomes a scroll', () => {
    const onPress = vi.fn();
    render(<CardHarness onPress={onPress} />);
    const card = screen.getByTestId('card') as HTMLElement;
    mockRect(card);

    dispatchPointer(card, 'pointerdown', { clientX: 60, clientY: 70, timeStamp: 10 });
    expect(card).toHaveAttribute('data-press-pulse', 'true');

    dispatchPointer(card, 'pointermove', { clientX: 60, clientY: 76, timeStamp: 20 });
    expect(card).not.toHaveAttribute('data-press-pulse');
    expect(card.style.getPropertyValue('--press-pulse-x')).toBe('');
  });

  it('cancels on horizontal movement', () => {
    const onPress = vi.fn();
    render(<CardHarness onPress={onPress} />);
    const card = screen.getByTestId('card');

    dispatchPointer(card, 'pointerdown', { clientX: 10, clientY: 10, timeStamp: 10 });
    dispatchPointer(card, 'pointermove', { clientX: 21, clientY: 10, timeStamp: 20 });
    dispatchPointer(card, 'pointerup', { clientX: 21, clientY: 10, timeStamp: 30 });

    expect(onPress).not.toHaveBeenCalled();
  });

  it('cancels long presses', () => {
    const onPress = vi.fn();
    render(<CardHarness onPress={onPress} />);
    const card = screen.getByTestId('card');

    dispatchPointer(card, 'pointerdown', { clientX: 10, clientY: 10, timeStamp: 10 });
    dispatchPointer(card, 'pointerup', { clientX: 10, clientY: 10, timeStamp: 520 });

    expect(onPress).not.toHaveBeenCalled();
  });

  it('does not use pointer capture', () => {
    const onPress = vi.fn();
    render(<CardHarness onPress={onPress} />);
    const card = screen.getByTestId('card') as HTMLElement;
    card.setPointerCapture = vi.fn();

    dispatchPointer(card, 'pointerdown', { clientX: 10, clientY: 10, timeStamp: 10 });
    dispatchPointer(card, 'pointerup', { clientX: 10, clientY: 10, timeStamp: 20 });

    expect(card.setPointerCapture).not.toHaveBeenCalled();
    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('ignores nested interactive descendants and data-press-stop elements', () => {
    const onPress = vi.fn();
    render(<CardHarness onPress={onPress} withNested />);
    const nested = screen.getByTestId('nested');
    const stop = screen.getByTestId('stop');

    dispatchPointer(nested, 'pointerdown', { clientX: 10, clientY: 10, timeStamp: 10 });
    dispatchPointer(nested, 'pointerup', { clientX: 10, clientY: 10, timeStamp: 20 });
    dispatchClick(nested, 30);

    dispatchPointer(stop, 'pointerdown', { clientX: 10, clientY: 10, timeStamp: 40 });
    dispatchPointer(stop, 'pointerup', { clientX: 10, clientY: 10, timeStamp: 50 });
    dispatchClick(stop, 60);

    expect(onPress).not.toHaveBeenCalled();
  });

  it('uses click for mouse fallback', () => {
    const onPress = vi.fn();
    render(<CardHarness onPress={onPress} />);
    const card = screen.getByTestId('card');

    dispatchPointer(card, 'pointerdown', { pointerType: 'mouse', clientX: 10, clientY: 10, timeStamp: 10 });
    dispatchPointer(card, 'pointerup', { pointerType: 'mouse', clientX: 10, clientY: 10, timeStamp: 20 });
    expect(onPress).not.toHaveBeenCalled();

    dispatchClick(card, 30);
    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('suppresses a compatibility click retargeted under an unmounted card target', () => {
    const onUnderlyingPress = vi.fn();
    render(<CardRetargetHarness onUnderlyingPress={onUnderlyingPress} />);
    const card = screen.getByTestId('card');

    dispatchPointer(card, 'pointerdown', { clientX: 20, clientY: 20, timeStamp: 10 });
    dispatchPointer(card, 'pointerup', { clientX: 20, clientY: 20, timeStamp: 20 });

    expect(screen.queryByTestId('card')).toBeNull();
    const syntheticClick = dispatchClick(screen.getByTestId('underlying'), { clientX: 20, clientY: 20, timeStamp: 80 });

    expect(syntheticClick.defaultPrevented).toBe(true);
    expect(onUnderlyingPress).not.toHaveBeenCalled();
  });

  it('supports keyboard activation and prevents Space scroll', () => {
    const onPress = vi.fn();
    render(<CardHarness onPress={onPress} />);
    const card = screen.getByTestId('card');

    const spaceEvent = new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true });
    fireEvent(card, spaceEvent);
    expect(spaceEvent.defaultPrevented).toBe(true);
    expect(onPress).toHaveBeenCalledTimes(1);

    fireEvent.keyDown(card, { key: 'Enter' });
    expect(onPress).toHaveBeenCalledTimes(2);
  });
});

describe('usePressAction', () => {
  it('fires on touch pointerup and suppresses the bubbling synthetic click when requested', () => {
    const onParentPress = vi.fn();
    const onPress = vi.fn();
    render(<ControlHarness onParentPress={onParentPress} onPress={onPress} />);
    const control = screen.getByTestId('control');

    dispatchPointer(control, 'pointerdown', { clientX: 4, clientY: 4, timeStamp: 10 });
    dispatchPointer(control, 'pointerup', { clientX: 4, clientY: 4, timeStamp: 20 });
    dispatchClick(control, 80);

    expect(onPress).toHaveBeenCalledTimes(1);
    expect(onParentPress).not.toHaveBeenCalled();
  });
});