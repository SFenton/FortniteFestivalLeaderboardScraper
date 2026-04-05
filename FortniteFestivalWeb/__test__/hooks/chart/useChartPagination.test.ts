import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useChartPagination } from '../../../src/hooks/chart/useChartPagination';
import type { ChartPoint } from '../../../src/hooks/chart/useChartData';

function point(i: number): ChartPoint {
  return {
    date: `2025-01-${String(i + 1).padStart(2, '0')}T00:00:00Z`,
    dateLabel: `1/${i + 1}/25`,
    timestamp: Date.parse(`2025-01-${String(i + 1).padStart(2, '0')}`),
    score: (i + 1) * 1000,
    accuracy: 0.95,
    isFullCombo: false,
  };
}

const identity = (a: ChartPoint, b: ChartPoint) => a.date === b.date && a.score === b.score;

describe('useChartPagination', () => {
  const data = Array.from({ length: 10 }, (_, i) => point(i));

  it('shows all data when maxBars >= data length', () => {
    const { result } = renderHook(() => useChartPagination(data, 20, 'guitar', identity));
    expect(result.current.visibleChartData).toHaveLength(10);
    expect(result.current.needsPagination).toBe(false);
  });

  it('slices to maxBars when data exceeds limit', () => {
    const { result } = renderHook(() => useChartPagination(data, 5, 'guitar', identity));
    expect(result.current.visibleChartData).toHaveLength(5);
    expect(result.current.needsPagination).toBe(true);
  });

  it('shows most recent entries by default (offset 0)', () => {
    const { result } = renderHook(() => useChartPagination(data, 5, 'guitar', identity));
    // offset 0 → show the last 5 entries
    expect(result.current.visibleChartData[4]!.score).toBe(10000);
    expect(result.current.visibleChartData[0]!.score).toBe(6000);
  });

  it('selectedPoint starts as null', () => {
    const { result } = renderHook(() => useChartPagination(data, 5, 'guitar', identity));
    expect(result.current.selectedPoint).toBeNull();
    expect(result.current.selectedIndex).toBe(-1);
  });

  it('setSelectedPoint updates selectedIndex', () => {
    const { result } = renderHook(() => useChartPagination(data, 10, 'guitar', identity));
    act(() => { result.current.setSelectedPoint(data[3]!); });
    expect(result.current.selectedIndex).toBe(3);
  });

  it('resets offset when instrument changes', () => {
    const { result, rerender } = renderHook(
      ({ inst }: { inst: string }) => useChartPagination(data, 5, inst, identity),
      { initialProps: { inst: 'guitar' } },
    );
    act(() => { result.current.setChartOffset(3); });
    expect(result.current.clampedOffset).toBe(3);

    rerender({ inst: 'bass' });
    expect(result.current.clampedOffset).toBe(0);
  });

  it('resets selectedPoint when instrument changes', () => {
    const { result, rerender } = renderHook(
      ({ inst }: { inst: string }) => useChartPagination(data, 10, inst, identity),
      { initialProps: { inst: 'guitar' } },
    );
    act(() => { result.current.setSelectedPoint(data[2]!); });
    expect(result.current.selectedPoint).not.toBeNull();

    rerender({ inst: 'bass' });
    expect(result.current.selectedPoint).toBeNull();
  });

  it('navigatePoint selects the target point', () => {
    const { result } = renderHook(() => useChartPagination(data, 10, 'guitar', identity));
    act(() => { result.current.navigatePoint(5); });
    expect(result.current.selectedPoint).toBe(data[5]);
    expect(result.current.selectedIndex).toBe(5);
  });

  it('navigatePoint clamps to valid range', () => {
    const { result } = renderHook(() => useChartPagination(data, 10, 'guitar', identity));
    act(() => { result.current.navigatePoint(-5); });
    expect(result.current.selectedIndex).toBe(0);

    act(() => { result.current.navigatePoint(999); });
    expect(result.current.selectedIndex).toBe(9);
  });

  it('backDisabled is true when offset at max (no selected point)', () => {
    const { result } = renderHook(() => useChartPagination(data, 5, 'guitar', identity));
    expect(result.current.backDisabled).toBe(false);
    act(() => { result.current.setChartOffset(5); }); // max offset = 10 - 5 = 5
    expect(result.current.backDisabled).toBe(true);
  });

  it('forwardDisabled is true when offset at 0 (no selected point)', () => {
    const { result } = renderHook(() => useChartPagination(data, 5, 'guitar', identity));
    expect(result.current.forwardDisabled).toBe(true); // already at offset 0
  });

  it('backDisabled is true when selected point is first', () => {
    const { result } = renderHook(() => useChartPagination(data, 10, 'guitar', identity));
    act(() => { result.current.navigatePoint(0); });
    expect(result.current.backDisabled).toBe(true);
  });

  it('forwardDisabled is true when selected point is last', () => {
    const { result } = renderHook(() => useChartPagination(data, 10, 'guitar', identity));
    act(() => { result.current.navigatePoint(9); });
    expect(result.current.forwardDisabled).toBe(true);
  });

  it('handles empty data', () => {
    const { result } = renderHook(() => useChartPagination([], 5, 'guitar', identity));
    expect(result.current.visibleChartData).toHaveLength(0);
    expect(result.current.needsPagination).toBe(false);
    expect(result.current.backDisabled).toBe(true);
    expect(result.current.forwardDisabled).toBe(true);
  });

  it('maxOffset and clampedOffset are correct', () => {
    const { result } = renderHook(() => useChartPagination(data, 3, 'guitar', identity));
    expect(result.current.maxOffset).toBe(7); // 10 - 3 = 7
    expect(result.current.clampedOffset).toBe(0);
    expect(result.current.pageStart).toBe(7); // 10 - 0 - 3 = 7
    expect(result.current.pageEnd).toBe(10);
  });

  it('navigatePoint adjusts offset when target is beyond visible window (forward)', () => {
    const { result } = renderHook(() => useChartPagination(data, 3, 'guitar', identity));
    // Set offset to show oldest entries
    act(() => { result.current.setChartOffset(7); });
    // Navigate to latest point (index 9) — beyond current window [0..2]
    act(() => { result.current.navigatePoint(9); });
    expect(result.current.selectedIndex).toBe(9);
    // Offset should adjust to show index 9 in the window
  });

  it('navigatePoint adjusts offset when target is before visible window (backward)', () => {
    const { result } = renderHook(() => useChartPagination(data, 3, 'guitar', identity));
    // Default offset 0 shows [7,8,9]. Navigate to index 0.
    act(() => { result.current.navigatePoint(0); });
    expect(result.current.selectedIndex).toBe(0);
  });
});
