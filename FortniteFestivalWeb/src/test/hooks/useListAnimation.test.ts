import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { ListPhase } from '@festival/core';
import type { ChartPoint } from '../../hooks/chart/useChartData';
import { useListAnimation } from '../../hooks/chart/useListAnimation';

function makePoint(overrides: Partial<ChartPoint> = {}): ChartPoint {
  return {
    date: '2024-01-01',
    dateLabel: 'Jan 1',
    timestamp: 1704067200000,
    score: 100000,
    accuracy: 95.0,
    isFullCombo: false,
    ...overrides,
  };
}

const CARD_HEIGHT = 48;
const CARD_GAP = 4;
const OUT_BASE_MS = 200;
const OUT_STEP_MS = 40;
const IN_BASE_MS = 300;
const IN_STEP_MS = 60;
const HEIGHT_TRANSITION_MS = 300;

function calcListHeight(n: number): number {
  return n > 0 ? n * CARD_HEIGHT + (n - 1) * CARD_GAP : 0;
}

describe('useListAnimation', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it('returns initial cards, Idle phase, and correct height', () => {
    const cards = [makePoint({ score: 100 }), makePoint({ score: 200 })];
    const { result } = renderHook(() => useListAnimation(cards));

    expect(result.current.displayedCards).toBe(cards);
    expect(result.current.listPhase).toBe(ListPhase.Idle);
    expect(result.current.listHeight).toBe(calcListHeight(2));
  });

  it('returns empty state when no cards given', () => {
    const { result } = renderHook(() => useListAnimation([]));

    expect(result.current.displayedCards).toEqual([]);
    expect(result.current.listPhase).toBe(ListPhase.Idle);
    expect(result.current.listHeight).toBe(0);
  });

  it('does not re-run effect when same reference is passed', () => {
    const cards = [makePoint()];
    const { result, rerender } = renderHook(() => useListAnimation(cards));

    rerender();
    expect(result.current.listPhase).toBe(ListPhase.Idle);
    expect(result.current.displayedCards).toBe(cards);
  });

  describe('transition from empty to cards', () => {
    it('enters In phase then settles to Idle', async () => {
      const empty: ChartPoint[] = [];
      const cards = [makePoint({ score: 100 }), makePoint({ score: 200 })];
      let currentCards = empty;

      const { result, rerender } = renderHook(() => useListAnimation(currentCards));
      expect(result.current.listPhase).toBe(ListPhase.Idle);

      // Switch to non-empty cards
      currentCards = cards;
      rerender();

      expect(result.current.listPhase).toBe(ListPhase.In);
      expect(result.current.displayedCards).toBe(cards);
      expect(result.current.listHeight).toBe(calcListHeight(2));

      // Wait for In duration: IN_BASE_MS + (2-1) * IN_STEP_MS = 360ms
      const inDuration = IN_BASE_MS + (cards.length - 1) * IN_STEP_MS;
      await act(async () => { vi.advanceTimersByTime(inDuration + 10); });

      expect(result.current.listPhase).toBe(ListPhase.Idle);
    });

    it('skips animation when skipAnimation is true', () => {
      const empty: ChartPoint[] = [];
      const cards = [makePoint()];
      let currentCards = empty;

      const { result, rerender } = renderHook(() => useListAnimation(currentCards, true));

      currentCards = cards;
      rerender();

      expect(result.current.listPhase).toBe(ListPhase.Idle);
      expect(result.current.displayedCards).toBe(cards);
      expect(result.current.listHeight).toBe(calcListHeight(1));
    });
  });

  describe('transition from cards to new (shrinking) cards', () => {
    it('goes through Out → height shrink → In → Idle', async () => {
      // Start with 5 cards, transition to 2 (height shrinks)
      const bigCards = Array.from({ length: 5 }, (_, i) => makePoint({ score: i * 1000 }));
      const smallCards = [makePoint({ score: 1 }), makePoint({ score: 2 })];
      let currentCards = bigCards;

      const { result, rerender } = renderHook(() => useListAnimation(currentCards));
      expect(result.current.listPhase).toBe(ListPhase.Idle);
      expect(result.current.listHeight).toBe(calcListHeight(5));

      // Change to smaller set
      currentCards = smallCards;
      rerender();

      // Should be in Out phase
      expect(result.current.listPhase).toBe(ListPhase.Out);

      // After out duration (200 + 4*40 = 360ms), height should shrink
      const outDuration = OUT_BASE_MS + (bigCards.length - 1) * OUT_STEP_MS;
      await act(async () => { vi.advanceTimersByTime(outDuration + 10); });

      expect(result.current.listHeight).toBe(calcListHeight(2));

      // After height transition (300ms), should be In
      await act(async () => { vi.advanceTimersByTime(HEIGHT_TRANSITION_MS + 10); });

      expect(result.current.listPhase).toBe(ListPhase.In);
      expect(result.current.displayedCards).toBe(smallCards);

      // After in duration, should be Idle
      const inDuration = IN_BASE_MS + (smallCards.length - 1) * IN_STEP_MS;
      await act(async () => { vi.advanceTimersByTime(inDuration + 10); });

      expect(result.current.listPhase).toBe(ListPhase.Idle);
    });
  });

  describe('transition from cards to new (growing) cards', () => {
    it('goes through Out → empty → height grow → In → Idle', async () => {
      // Start with 2 cards, transition to 5 (height grows)
      const smallCards = [makePoint({ score: 1 }), makePoint({ score: 2 })];
      const bigCards = Array.from({ length: 5 }, (_, i) => makePoint({ score: i * 1000 }));
      let currentCards = smallCards;

      // Mock requestAnimationFrame for the growing path
      vi.spyOn(globalThis, 'requestAnimationFrame').mockImplementation((cb) => {
        cb(0);
        return 0;
      });

      const { result, rerender } = renderHook(() => useListAnimation(currentCards));
      expect(result.current.listPhase).toBe(ListPhase.Idle);
      expect(result.current.listHeight).toBe(calcListHeight(2));

      // Change to larger set
      currentCards = bigCards;
      rerender();

      expect(result.current.listPhase).toBe(ListPhase.Out);

      // After out duration (200 + 1*40 = 240ms), rAF fires, cards cleared, height grows
      const outDuration = OUT_BASE_MS + (smallCards.length - 1) * OUT_STEP_MS;
      await act(async () => { vi.advanceTimersByTime(outDuration + 10); });

      expect(result.current.listHeight).toBe(calcListHeight(5));

      // After height transition, should enter In phase with new cards
      await act(async () => { vi.advanceTimersByTime(HEIGHT_TRANSITION_MS + 10); });

      expect(result.current.listPhase).toBe(ListPhase.In);
      expect(result.current.displayedCards).toBe(bigCards);

      // After in duration, should be Idle
      const inDuration = IN_BASE_MS + (bigCards.length - 1) * IN_STEP_MS;
      await act(async () => { vi.advanceTimersByTime(inDuration + 10); });

      expect(result.current.listPhase).toBe(ListPhase.Idle);

      vi.mocked(globalThis.requestAnimationFrame).mockRestore();
    });
  });

  describe('skipAnimation with existing cards', () => {
    it('jumps directly to Idle with new cards', () => {
      const old = [makePoint({ score: 1 })];
      const next = [makePoint({ score: 2 }), makePoint({ score: 3 })];
      let current = old;

      const { result, rerender } = renderHook(() => useListAnimation(current, true));
      expect(result.current.listPhase).toBe(ListPhase.Idle);

      current = next;
      rerender();

      expect(result.current.listPhase).toBe(ListPhase.Idle);
      expect(result.current.displayedCards).toBe(next);
      expect(result.current.listHeight).toBe(calcListHeight(2));
    });
  });

  describe('rapid changes cancel timers', () => {
    it('final set of cards wins after rapid instrument switches', async () => {
      vi.spyOn(globalThis, 'requestAnimationFrame').mockImplementation((cb) => {
        cb(0);
        return 0;
      });

      const a = [makePoint({ score: 1 })];
      const b = [makePoint({ score: 2 }), makePoint({ score: 3 })];
      const c = [makePoint({ score: 4 }), makePoint({ score: 5 }), makePoint({ score: 6 })];
      let current = a;

      const { result, rerender } = renderHook(() => useListAnimation(current));

      // Rapid switch a → b → c
      current = b;
      rerender();
      expect(result.current.listPhase).toBe(ListPhase.Out);

      current = c;
      rerender();
      // Previous timers should be cleared

      // Let the final transition complete
      const outDuration = OUT_BASE_MS + (a.length - 1) * OUT_STEP_MS;
      await act(async () => { vi.advanceTimersByTime(outDuration + 10); });
      await act(async () => { vi.advanceTimersByTime(HEIGHT_TRANSITION_MS + 10); });

      const inDuration = IN_BASE_MS + (c.length - 1) * IN_STEP_MS;
      await act(async () => { vi.advanceTimersByTime(inDuration + 10); });

      expect(result.current.displayedCards).toBe(c);
      expect(result.current.listPhase).toBe(ListPhase.Idle);
      expect(result.current.listHeight).toBe(calcListHeight(3));

      vi.mocked(globalThis.requestAnimationFrame).mockRestore();
    });
  });
});
