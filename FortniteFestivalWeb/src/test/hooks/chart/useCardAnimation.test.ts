import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { CardPhase } from '@festival/core';
import type { ChartPoint } from '../../../hooks/chart/useChartData';
import { useCardAnimation } from '../../../hooks/chart/useCardAnimation';

function makePoint(overrides: Partial<ChartPoint> = {}): ChartPoint {
  return {
    date: '2024-01-15',
    dateLabel: 'Jan 15',
    timestamp: 1705276800000,
    score: 120000,
    accuracy: 96.5,
    isFullCombo: false,
    ...overrides,
  };
}

describe('useCardAnimation', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    // requestAnimationFrame fires synchronously for test purposes
    vi.spyOn(globalThis, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
  });
  afterEach(() => {
    vi.mocked(globalThis.requestAnimationFrame).mockRestore();
    vi.useRealTimers();
  });

  it('starts in Closed phase with null displayedPoint', () => {
    const { result } = renderHook(() => useCardAnimation(null));

    expect(result.current.displayedPoint).toBeNull();
    expect(result.current.cardPhase).toBe(CardPhase.Closed);
    expect(result.current.cardHeight).toBe(0);
    expect(result.current.cardContentRef).toBeDefined();
  });

  describe('opening from closed', () => {
    it('transitions Closed → Growing → Open when a point is selected', async () => {
      const point = makePoint();
      let selected: ChartPoint | null = null;

      const { result, rerender } = renderHook(() => useCardAnimation(selected));
      expect(result.current.cardPhase).toBe(CardPhase.Closed);

      // Select a point
      selected = point;
      rerender();

      // rAF fires synchronously, so we should now be Growing
      expect(result.current.displayedPoint).toBe(point);
      expect(result.current.cardPhase).toBe(CardPhase.Growing);

      // After 250ms, becomes Open
      await act(async () => { vi.advanceTimersByTime(260); });
      expect(result.current.cardPhase).toBe(CardPhase.Open);
    });

    it('measures cardContentRef offsetHeight when opening', () => {
      const point = makePoint();
      let selected: ChartPoint | null = null;

      const { result, rerender } = renderHook(() => useCardAnimation(selected));

      // Simulate a real DOM element with offsetHeight
      const fakeDiv = document.createElement('div');
      Object.defineProperty(fakeDiv, 'offsetHeight', { value: 120 });
      (result.current.cardContentRef as React.MutableRefObject<HTMLDivElement | null>).current = fakeDiv;

      selected = point;
      rerender();

      // cardHeight = offsetHeight + 2 = 122
      expect(result.current.cardHeight).toBe(122);
    });
  });

  describe('closing from open', () => {
    it('transitions Open → Fading → Shrinking → Closed', async () => {
      const point = makePoint();
      let selected: ChartPoint | null = point;

      const { result, rerender } = renderHook(() => useCardAnimation(selected));

      // Open the card
      await act(async () => { vi.advanceTimersByTime(260); });
      expect(result.current.cardPhase).toBe(CardPhase.Open);

      // Close
      selected = null;
      rerender();

      expect(result.current.cardPhase).toBe(CardPhase.Fading);

      // After 200ms, Shrinking
      await act(async () => { vi.advanceTimersByTime(210); });
      expect(result.current.cardPhase).toBe(CardPhase.Shrinking);

      // After 250ms more, Closed
      await act(async () => { vi.advanceTimersByTime(260); });
      expect(result.current.cardPhase).toBe(CardPhase.Closed);
      expect(result.current.displayedPoint).toBeNull();
    });
  });

  describe('swapping points while open', () => {
    it('transitions Open → SwapOut → SwapIn → Open with new point', async () => {
      const pointA = makePoint({ score: 100000 });
      const pointB = makePoint({ score: 200000 });
      let selected: ChartPoint | null = pointA;

      const { result, rerender } = renderHook(() => useCardAnimation(selected));

      // Open
      await act(async () => { vi.advanceTimersByTime(260); });
      expect(result.current.cardPhase).toBe(CardPhase.Open);
      expect(result.current.displayedPoint).toBe(pointA);

      // Swap to pointB
      selected = pointB;
      rerender();

      expect(result.current.cardPhase).toBe(CardPhase.SwapOut);

      // After 150ms, SwapIn with new point
      await act(async () => { vi.advanceTimersByTime(160); });
      expect(result.current.cardPhase).toBe(CardPhase.SwapIn);
      expect(result.current.displayedPoint).toBe(pointB);

      // After 150ms more, Open
      await act(async () => { vi.advanceTimersByTime(160); });
      expect(result.current.cardPhase).toBe(CardPhase.Open);
    });

    it('handles swap during SwapIn phase', async () => {
      const pointA = makePoint({ score: 100000 });
      const pointB = makePoint({ score: 200000 });
      const pointC = makePoint({ score: 300000 });
      let selected: ChartPoint | null = pointA;

      const { result, rerender } = renderHook(() => useCardAnimation(selected));

      // Open
      await act(async () => { vi.advanceTimersByTime(260); });
      expect(result.current.cardPhase).toBe(CardPhase.Open);

      // Swap to B
      selected = pointB;
      rerender();
      expect(result.current.cardPhase).toBe(CardPhase.SwapOut);

      // Before swap completes, swap to C
      selected = pointC;
      rerender();

      // Timers from B should be cancelled, new swap starts from SwapOut
      expect(result.current.cardPhase).toBe(CardPhase.SwapOut);

      // After 150ms, SwapIn with C (the latest pending point)
      await act(async () => { vi.advanceTimersByTime(160); });
      expect(result.current.cardPhase).toBe(CardPhase.SwapIn);
      expect(result.current.displayedPoint).toBe(pointC);

      // After 150ms more, Open
      await act(async () => { vi.advanceTimersByTime(160); });
      expect(result.current.cardPhase).toBe(CardPhase.Open);
    });
  });

  describe('null → null does nothing', () => {
    it('stays Closed', () => {
      let selected: ChartPoint | null = null;
      const { result, rerender } = renderHook(() => useCardAnimation(selected));

      rerender();
      expect(result.current.cardPhase).toBe(CardPhase.Closed);
      expect(result.current.displayedPoint).toBeNull();
    });
  });

  describe('rapid open/close cancels timers', () => {
    it('opens then immediately closes before Growing → Open', async () => {
      const point = makePoint();
      let selected: ChartPoint | null = null;

      const { result, rerender } = renderHook(() => useCardAnimation(selected));

      // Open
      selected = point;
      rerender();
      expect(result.current.cardPhase).toBe(CardPhase.Growing);

      // Immediately close before the 250ms timer fires
      selected = null;
      rerender();

      // Should start closing (Fading)
      expect(result.current.cardPhase).toBe(CardPhase.Fading);

      // Let everything settle
      await act(async () => { vi.advanceTimersByTime(500); });
      expect(result.current.cardPhase).toBe(CardPhase.Closed);
      expect(result.current.displayedPoint).toBeNull();
    });
  });
});
