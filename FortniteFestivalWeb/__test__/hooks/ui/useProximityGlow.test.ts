import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useProximityGlow } from '../../../src/hooks/ui/useProximityGlow';

let queuedFrame: FrameRequestCallback | null = null;

describe('useProximityGlow', () => {
  afterEach(() => {
    document.body.innerHTML = '';
    queuedFrame = null;
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('measures only the hovered frosted card and clears the previous card', () => {
    stubAnimationFrame();
    const first = createFrostedCard(10, 20);
    const second = createFrostedCard(100, 200);
    document.body.append(first.card, second.card);
    const { unmount } = renderHook(() => useProximityGlow(true));

    first.child.dispatchEvent(new MouseEvent('mousemove', {
      bubbles: true,
      clientX: 30,
      clientY: 50,
    }));
    flushAnimationFrame();
    expect(first.rect).toHaveBeenCalledTimes(1);
    expect(second.rect).not.toHaveBeenCalled();
    expect(first.card.style.getPropertyValue('--glow-x')).toBe('20px');
    expect(first.card.style.getPropertyValue('--glow-y')).toBe('30px');
    expect(first.card.style.getPropertyValue('--glow-opacity')).toBe('1');

    second.child.dispatchEvent(new MouseEvent('mousemove', {
      bubbles: true,
      clientX: 130,
      clientY: 240,
    }));
    flushAnimationFrame();
    expect(first.rect).toHaveBeenCalledTimes(1);
    expect(second.rect).toHaveBeenCalledTimes(1);
    expect(first.card.style.getPropertyValue('--glow-opacity')).toBe('0');
    expect(second.card.style.getPropertyValue('--glow-opacity')).toBe('1');

    document.documentElement.dispatchEvent(new MouseEvent('mouseleave'));
    expect(second.card.style.getPropertyValue('--glow-opacity')).toBe('0');
    unmount();
  });

  it('suppresses cards outside the active scope and clears on disable', () => {
    stubAnimationFrame();
    const outside = createFrostedCard(0, 0);
    const inside = createFrostedCard(50, 60);
    const scope = document.createElement('div');
    scope.dataset.glowScope = '';
    scope.appendChild(inside.card);
    document.body.append(outside.card, scope);
    const { rerender } = renderHook(({ enabled }) => useProximityGlow(enabled), {
      initialProps: { enabled: true },
    });

    outside.child.dispatchEvent(new MouseEvent('mousemove', {
      bubbles: true,
      clientX: 10,
      clientY: 10,
    }));
    flushAnimationFrame();
    expect(outside.rect).not.toHaveBeenCalled();
    expect(outside.card.style.getPropertyValue('--glow-opacity')).toBe('');

    inside.child.dispatchEvent(new MouseEvent('mousemove', {
      bubbles: true,
      clientX: 70,
      clientY: 90,
    }));
    flushAnimationFrame();
    expect(inside.rect).toHaveBeenCalledTimes(1);
    expect(inside.card.style.getPropertyValue('--glow-opacity')).toBe('1');

    rerender({ enabled: false });
    expect(inside.card.style.getPropertyValue('--glow-opacity')).toBe('0');
  });
});

function stubAnimationFrame(): void {
  vi.stubGlobal('requestAnimationFrame', vi.fn((callback: FrameRequestCallback) => {
    queuedFrame = callback;
    return 1;
  }));
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
}

function flushAnimationFrame(): void {
  const frame = queuedFrame;
  queuedFrame = null;
  frame?.(0);
}

function createFrostedCard(left: number, top: number): {
  card: HTMLDivElement;
  child: HTMLSpanElement;
  rect: ReturnType<typeof vi.fn>;
} {
  const card = document.createElement('div');
  card.style.setProperty('--frosted-card', '1');
  const child = document.createElement('span');
  card.appendChild(child);
  const rect = vi.fn(() => ({
    left,
    top,
    right: left + 100,
    bottom: top + 100,
    width: 100,
    height: 100,
    x: left,
    y: top,
    toJSON: () => '',
  }));
  card.getBoundingClientRect = rect;
  return { card, child, rect };
}
