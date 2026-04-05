/**
 * Hook managing offset-based chart pagination with optional point navigation.
 * Generic over any data-point type — callers supply an identity function.
 */
import { useState, useEffect, useMemo, useCallback } from 'react';

export function useChartPagination<T>(
  chartData: T[],
  maxBars: number,
  selectedInstrument: string,
  identity: (a: T, b: T) => boolean,
) {
  const [chartOffset, setChartOffset] = useState(0);
  const [selectedPoint, setSelectedPoint] = useState<T | null>(null);

  // Reset offset and selection when instrument changes
  useEffect(() => { setChartOffset(0); }, [selectedInstrument]);
  useEffect(() => { setSelectedPoint(null); }, [selectedInstrument]);

  const maxOffset = Math.max(0, chartData.length - maxBars);
  const clampedOffset = Math.min(chartOffset, maxOffset);
  const pageEnd = chartData.length - clampedOffset;
  const pageStart = Math.max(0, pageEnd - maxBars);
  const visibleChartData = chartData.slice(pageStart, pageEnd);
  const needsPagination = chartData.length > maxBars;

  const selectedIndex = useMemo(() => {
    if (!selectedPoint) return -1;
    return chartData.findIndex(p => identity(p, selectedPoint));
  }, [selectedPoint, chartData, identity]);

  /* v8 ignore start -- navigatePoint: offset adjustment logic */
  const navigatePoint = useCallback((targetIdx: number) => {
    const clamped = Math.max(0, Math.min(targetIdx, chartData.length - 1));
    const point = chartData[clamped];
    setSelectedPoint(point ?? null);
    setChartOffset(prev => {
      const curEnd = chartData.length - Math.min(prev, maxOffset);
      const curStart = Math.max(0, curEnd - maxBars);
      if (clamped >= curStart && clamped < curEnd) return prev;
      if (clamped < curStart) {
        return Math.min(chartData.length - clamped - maxBars, maxOffset);
      }
      return Math.max(chartData.length - clamped - 1, 0);
    });
  }, [chartData, maxBars, maxOffset]);
  /* v8 ignore stop */

  const backDisabled = selectedPoint
    ? selectedIndex <= 0
    : clampedOffset >= maxOffset;
  const forwardDisabled = selectedPoint
    ? selectedIndex >= chartData.length - 1
    : clampedOffset <= 0;

  return {
    chartOffset,
    setChartOffset,
    selectedPoint,
    setSelectedPoint,
    selectedIndex,
    visibleChartData,
    needsPagination,
    navigatePoint,
    backDisabled,
    forwardDisabled,
    maxOffset,
    clampedOffset,
    pageStart,
    pageEnd,
  };
}
