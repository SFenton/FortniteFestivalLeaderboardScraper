import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useChartDimensions } from '../../hooks/chart/useChartDimensions';
import { MIN_BAR_WIDTH, BAR_GAP, FALLBACK_OVERHEAD } from '../../pages/songinfo/components/chart/chartConstants';

// Mock ResizeObserver
let resizeCallback: ResizeObserverCallback | null = null;
let observedElements: Element[] = [];

class MockResizeObserver {
  constructor(cb: ResizeObserverCallback) {
    resizeCallback = cb;
  }
  observe(el: Element) {
    observedElements.push(el);
  }
  unobserve() {}
  disconnect() {
    observedElements = [];
  }
}

beforeEach(() => {
  resizeCallback = null;
  observedElements = [];
  vi.stubGlobal('ResizeObserver', MockResizeObserver);
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => {
    cb(0);
    return 0;
  });
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('useChartDimensions', () => {
  it('returns Infinity maxBars when container width is 0', () => {
    const ref = { current: document.createElement('div') };
    const { result } = renderHook(() => useChartDimensions(ref));
    expect(result.current.maxBars).toBe(Infinity);
  });

  it('returns correct maxBars after ResizeObserver fires', () => {
    const el = document.createElement('div');
    const ref = { current: el };
    const { result } = renderHook(() => useChartDimensions(ref));

    // Simulate ResizeObserver callback
    act(() => {
      if (resizeCallback) {
        resizeCallback(
          [{ target: el, contentRect: { width: 800, height: 600 } } as unknown as ResizeObserverEntry],
          {} as ResizeObserver,
        );
      }
    });

    // With width 800 and fallback overhead, compute expected maxBars
    const plotWidth = Math.max(0, 800 - FALLBACK_OVERHEAD);
    const expected = Math.max(1, Math.floor((plotWidth + BAR_GAP) / (MIN_BAR_WIDTH + BAR_GAP)));
    expect(result.current.maxBars).toBe(expected);
    expect(result.current.containerWidth).toBe(800);
  });

  it('updates containerWidth on subsequent ResizeObserver changes', () => {
    const el = document.createElement('div');
    const ref = { current: el };
    const { result } = renderHook(() => useChartDimensions(ref));

    act(() => {
      if (resizeCallback) {
        resizeCallback(
          [{ target: el, contentRect: { width: 600, height: 400 } } as unknown as ResizeObserverEntry],
          {} as ResizeObserver,
        );
      }
    });
    expect(result.current.containerWidth).toBe(600);

    act(() => {
      if (resizeCallback) {
        resizeCallback(
          [{ target: el, contentRect: { width: 1200, height: 400 } } as unknown as ResizeObserverEntry],
          {} as ResizeObserver,
        );
      }
    });
    expect(result.current.containerWidth).toBe(1200);
  });

  it('returns at least 1 for maxBars even with tiny container', () => {
    const el = document.createElement('div');
    const ref = { current: el };
    const { result } = renderHook(() => useChartDimensions(ref));

    act(() => {
      if (resizeCallback) {
        resizeCallback(
          [{ target: el, contentRect: { width: 50, height: 100 } } as unknown as ResizeObserverEntry],
          {} as ResizeObserver,
        );
      }
    });
    expect(result.current.maxBars).toBeGreaterThanOrEqual(1);
  });

  it('handles null ref gracefully', () => {
    const ref = { current: null };
    const { result } = renderHook(() => useChartDimensions(ref));
    expect(result.current.containerWidth).toBe(0);
    expect(result.current.maxBars).toBe(Infinity);
  });

  it('disconnects ResizeObserver on unmount', () => {
    const el = document.createElement('div');
    const ref = { current: el };
    const { unmount } = renderHook(() => useChartDimensions(ref));
    expect(observedElements.length).toBe(1);
    unmount();
    // disconnect was called so observedElements cleared
    expect(observedElements.length).toBe(0);
  });

  it('computes maxBars correctly with known overhead', () => {
    const el = document.createElement('div');
    // Add a mock clipPath rect to allow overhead calculation
    el.innerHTML = '<svg class="recharts-surface"><defs><clipPath><rect width="600" /></clipPath></defs></svg>';
    const ref = { current: el };
    const { result } = renderHook(() => useChartDimensions(ref));

    act(() => {
      if (resizeCallback) {
        resizeCallback(
          [{ target: el, contentRect: { width: 800, height: 600 } } as unknown as ResizeObserverEntry],
          {} as ResizeObserver,
        );
      }
    });

    // With clipW = 600, overhead = 800 - 600 = 200
    // plotWidth = 800 - 200 = 600
    // maxBars = floor((600 + 8) / (96 + 8)) = floor(608/104) = 5
    expect(result.current.maxBars).toBe(5);
  });
});
