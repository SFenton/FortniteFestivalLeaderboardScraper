import { useRef } from 'react';
import { render, renderHook, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ScrollContainerProvider, useScrollContainer } from '../../../src/contexts/ScrollContainerContext';
import { usePageWheelHandoff, usePanelWheelHandoff } from '../../../src/hooks/ui/useWheelHandoff';

function Harness() {
  const page = useScrollContainer();
  const panel = useRef<HTMLDivElement>(null);
  usePageWheelHandoff(page);
  usePanelWheelHandoff(panel);
  return <div><div ref={page} data-testid="page" /><div ref={panel} data-testid="panel" /></div>;
}

function mount() {
  return render(<ScrollContainerProvider><Harness /></ScrollContainerProvider>);
}

function wheel(target: Element, options: WheelEventInit = {}) {
  const event = new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY: 120, ...options });
  target.dispatchEvent(event);
  return event;
}

describe('Firefox native wheel handoff', () => {
  beforeEach(() => vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 Firefox/153.0'));
  afterEach(() => vi.restoreAllMocks());

  it('consumes only the page-to-panel handoff and leaves subsequent input native', () => {
    mount();
    const page = screen.getByTestId('page');
    const panel = screen.getByTestId('panel');
    expect(wheel(panel).defaultPrevented).toBe(false);
    expect(wheel(page).defaultPrevented).toBe(false);
    expect(wheel(panel).defaultPrevented).toBe(true);
    expect(wheel(panel).defaultPrevented).toBe(false);
  });

  it('does not intercept zoom and resets ownership for pointer/key actions', () => {
    mount();
    const page = screen.getByTestId('page');
    const panel = screen.getByTestId('panel');
    wheel(page, { ctrlKey: true });
    expect(wheel(panel).defaultPrevented).toBe(false);
    wheel(page);
    expect(wheel(panel, { ctrlKey: true }).defaultPrevented).toBe(false);
    expect(wheel(panel).defaultPrevented).toBe(false);
    for (const type of ['pointerdown', 'keydown']) {
      wheel(page);
      panel.dispatchEvent(new Event(type, { bubbles: true }));
      expect(wheel(panel).defaultPrevented).toBe(false);
    }
  });

  it('does not consume noncancelable input and removes listeners on unmount', () => {
    const { unmount } = mount();
    const page = screen.getByTestId('page');
    const panel = screen.getByTestId('panel');
    wheel(page);
    expect(wheel(panel, { cancelable: false }).defaultPrevented).toBe(false);
    expect(wheel(panel).defaultPrevented).toBe(true);
    unmount();
    wheel(page);
    expect(wheel(panel).defaultPrevented).toBe(false);
  });

  it('leaves other engines unchanged and tolerates unmounted refs', () => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 Chrome/151.0');
    mount();
    wheel(screen.getByTestId('page'));
    expect(wheel(screen.getByTestId('panel')).defaultPrevented).toBe(false);
    expect(() => renderHook(() => {
      usePageWheelHandoff({ current: null });
      usePanelWheelHandoff({ current: null });
    })).not.toThrow();
  });
});
