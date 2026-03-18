/**
 * Hook that measures a chart container's width and derives how many bars fit.
 * Learns the true Recharts axes overhead from the first render and locks it.
 */
import { useRef, useState, useEffect } from 'react';
import { MIN_BAR_WIDTH, BAR_GAP, FALLBACK_OVERHEAD } from '../../pages/songinfo/components/chart/chartConstants';

export function useChartDimensions(chartContainerRef: React.RefObject<HTMLDivElement | null>) {
  const [containerWidth, setContainerWidth] = useState(0);
  const axesOverheadRef = useRef<number | null>(null);

  useEffect(() => {
    const el = chartContainerRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;
      const w = entry.contentRect.width;
      setContainerWidth(w);
      if (axesOverheadRef.current === null) {
        const clip = el.querySelector('.recharts-surface clipPath rect');
        if (clip) {
          const clipW = parseFloat(clip.getAttribute('width') || '0');
          if (clipW > 0) {
            axesOverheadRef.current = w - clipW;
          }
        }
      }
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [chartContainerRef]);

  useEffect(() => {
    if (axesOverheadRef.current !== null || containerWidth === 0) return;
    const raf = requestAnimationFrame(() => {
      const el = chartContainerRef.current;
      if (!el) return;
      const clip = el.querySelector('.recharts-surface clipPath rect');
      if (clip) {
        const clipW = parseFloat(clip.getAttribute('width') || '0');
        if (clipW > 0) {
          axesOverheadRef.current = containerWidth - clipW;
          setContainerWidth((prev) => prev);
        }
      }
    });
    return () => cancelAnimationFrame(raf);
  });

  const overhead = axesOverheadRef.current ?? FALLBACK_OVERHEAD;
  const plotWidth = Math.max(0, containerWidth - overhead);
  const maxBars = containerWidth === 0
    ? Infinity
    : Math.max(1, Math.floor((plotWidth + BAR_GAP) / (MIN_BAR_WIDTH + BAR_GAP)));

  return { chartContainerRef, containerWidth, maxBars };
}
