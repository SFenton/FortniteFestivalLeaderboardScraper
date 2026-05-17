import { act, fireEvent, render, screen } from '@testing-library/react';
import { useState, type ComponentProps } from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { useNavLinkPress } from '../../../src/hooks/navigation/useNavLinkPress';

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

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname}</div>;
}

function TestLink({
  disabled,
  download,
  onNavigate = vi.fn(),
  target,
  to = '/target',
}: {
  disabled?: boolean;
  download?: boolean | string;
  onNavigate?: () => void;
  target?: string;
  to?: string;
}) {
  const linkPress = useNavLinkPress<HTMLAnchorElement>({
    to,
    disabled,
    download,
    onNavigate,
    target,
  });

  return (
    <a
      href="#"
      target={target}
      download={download}
      data-testid="link"
      data-pressed={linkPress.isPressed ? 'true' : undefined}
      {...linkPress.linkPressHandlers}
    >
      Go
    </a>
  );
}

function RetargetLinkHarness({ onUnderlyingPress }: { onUnderlyingPress: () => void }) {
  const [linkOpen, setLinkOpen] = useState(true);
  const linkPress = useNavLinkPress<HTMLAnchorElement>({
    to: '/target',
    onNavigate: () => setLinkOpen(false),
  });

  return (
    <>
      <button type="button" data-testid="underlying" onClick={onUnderlyingPress}>Underlying</button>
      {linkOpen && (
        <a href="#" data-testid="retarget-link" {...linkPress.linkPressHandlers}>
          Go
        </a>
      )}
      <LocationProbe />
    </>
  );
}

function renderLink(props: ComponentProps<typeof TestLink> = {}) {
  return render(
    <MemoryRouter initialEntries={['/start']}>
      <TestLink {...props} />
      <LocationProbe />
    </MemoryRouter>,
  );
}

function renderRetargetLink(onUnderlyingPress: () => void) {
  return render(
    <MemoryRouter initialEntries={['/start']}>
      <RetargetLinkHarness onUnderlyingPress={onUnderlyingPress} />
    </MemoryRouter>,
  );
}

function mockRect(element: Element) {
  vi.spyOn(element, 'getBoundingClientRect').mockReturnValue({
    x: 10,
    y: 20,
    left: 10,
    top: 20,
    right: 210,
    bottom: 120,
    width: 200,
    height: 100,
    toJSON: () => ({}),
  } as DOMRect);
}

describe('useNavLinkPress', () => {
  it('navigates internal touch links on pointerup and suppresses the synthetic click', () => {
    const onNavigate = vi.fn();
    renderLink({ onNavigate });
    const link = screen.getByTestId('link');

    fireEvent.pointerDown(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 10 });
    expect(link).toHaveAttribute('data-pressed', 'true');

    fireEvent.pointerUp(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 10 });
    expect(screen.getByTestId('location')).toHaveTextContent('/target');
    expect(onNavigate).toHaveBeenCalledTimes(1);
    expect(link).not.toHaveAttribute('data-pressed');

    fireEvent.click(link);
    expect(onNavigate).toHaveBeenCalledTimes(1);
  });

  it('adds neutral press pulse metadata for intercepted touch links', () => {
    vi.useFakeTimers();
    try {
      const onNavigate = vi.fn();
      renderLink({ onNavigate });
      const link = screen.getByTestId('link') as HTMLElement;
      mockRect(link);

      fireEvent.pointerDown(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 80, clientY: 75 });
      expect(link).toHaveAttribute('data-press-pulse', 'true');
      expect(link.style.getPropertyValue('--press-pulse-x')).toBe('70px');
      expect(link.style.getPropertyValue('--press-pulse-y')).toBe('55px');

      fireEvent.pointerUp(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 80, clientY: 75 });
      expect(link).toHaveAttribute('data-press-pulse', 'true');
      expect(screen.getByTestId('location')).toHaveTextContent('/target');

      act(() => { vi.advanceTimersByTime(1000); });
      expect(link).not.toHaveAttribute('data-press-pulse');
    } finally {
      vi.useRealTimers();
    }
  });

  it('suppresses a compatibility click retargeted under an unmounted nav link', () => {
    const onUnderlyingPress = vi.fn();
    renderRetargetLink(onUnderlyingPress);
    const link = screen.getByTestId('retarget-link');

    dispatchPointer(link, 'pointerdown', { clientX: 20, clientY: 20, timeStamp: 10 });
    dispatchPointer(link, 'pointerup', { clientX: 20, clientY: 20, timeStamp: 20 });

    expect(screen.getByTestId('location')).toHaveTextContent('/target');
    expect(screen.queryByTestId('retarget-link')).toBeNull();
    const syntheticClick = dispatchClick(screen.getByTestId('underlying'), { clientX: 20, clientY: 20, timeStamp: 80 });

    expect(syntheticClick.defaultPrevented).toBe(true);
    expect(onUnderlyingPress).not.toHaveBeenCalled();
  });

  it('allows a later real click after nav compatibility-click suppression expires', () => {
    const onUnderlyingPress = vi.fn();
    renderRetargetLink(onUnderlyingPress);
    const link = screen.getByTestId('retarget-link');

    dispatchPointer(link, 'pointerdown', { clientX: 20, clientY: 20, timeStamp: 10 });
    dispatchPointer(link, 'pointerup', { clientX: 20, clientY: 20, timeStamp: 20 });
    dispatchClick(screen.getByTestId('underlying'), { clientX: 20, clientY: 20, timeStamp: 900 });

    expect(onUnderlyingPress).toHaveBeenCalledTimes(1);
  });

  it('allows an immediate click outside the nav compatibility-click suppression radius', () => {
    const onUnderlyingPress = vi.fn();
    renderRetargetLink(onUnderlyingPress);
    const link = screen.getByTestId('retarget-link');

    dispatchPointer(link, 'pointerdown', { clientX: 20, clientY: 20, timeStamp: 10 });
    dispatchPointer(link, 'pointerup', { clientX: 20, clientY: 20, timeStamp: 20 });
    const realClick = dispatchClick(screen.getByTestId('underlying'), { clientX: 80, clientY: 20, timeStamp: 80 });

    expect(realClick.defaultPrevented).toBe(false);
    expect(onUnderlyingPress).toHaveBeenCalledTimes(1);
  });

  it('cancels pointer navigation when movement exceeds the threshold', () => {
    const onNavigate = vi.fn();
    renderLink({ onNavigate });
    const link = screen.getByTestId('link');

    fireEvent.pointerDown(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 10 });
    fireEvent.pointerMove(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 40 });
    fireEvent.pointerUp(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 40 });

    expect(screen.getByTestId('location')).toHaveTextContent('/start');
    expect(onNavigate).not.toHaveBeenCalled();
    expect(link).not.toHaveAttribute('data-pressed');
  });

  it('leaves new-tab and download links to native browser behavior', () => {
    const newTabNavigate = vi.fn();
    renderLink({ onNavigate: newTabNavigate, target: '_blank' });
    const newTabLink = screen.getByTestId('link');

    fireEvent.pointerDown(newTabLink, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(newTabLink, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 10 });

    expect(screen.getByTestId('location')).toHaveTextContent('/start');
    expect(newTabNavigate).not.toHaveBeenCalled();
  });

  it('still runs the fallback navigation hook for normal mouse clicks', () => {
    const onNavigate = vi.fn();
    renderLink({ onNavigate });

    fireEvent.click(screen.getByTestId('link'));

    expect(onNavigate).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('location')).toHaveTextContent('/start');
  });
});
