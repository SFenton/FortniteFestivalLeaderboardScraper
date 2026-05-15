import { useEffect, useRef, useState, type CSSProperties, createElement } from 'react';
import cls from './MarqueeText.module.css';

export interface MarqueeTextProps {
  /** The text to display (and scroll when it overflows). */
  text: string;
  /** Semantic HTML element to wrap the text. */
  as?: 'h1' | 'p' | 'span';
  /** Extra class name forwarded to the container (or plain element when not scrolling). */
  className?: string;
  /** Extra inline styles forwarded to the container (or plain element when not scrolling). */
  style?: CSSProperties;
  /** Fixed cycle duration in seconds for one full scroll+pause cycle (default 8). All instances share the same cycle so cross-card phase alignment works via the global epoch. */
  cycleDuration?: number;
  /** Gap in pixels between the two text copies (default 28). */
  gap?: number;
  /** Override translate distance (pixels) for synchronized scrolling. */
  syncDistance?: number;
  /** Treat text as overflowing when it is within this many pixels of the container edge. */
  overflowInset?: number;
  /** Called with the measured text width when overflow is detected (0 when not overflowing). */
  onMeasure?: (textWidth: number) => void;
}

const DEFAULT_CYCLE = 8;
const DEFAULT_GAP = 28;
/** Global epoch for cross-card phase alignment (set once at module load). */
const MARQUEE_EPOCH = Date.now();

function getTextWidth(element: HTMLElement) {
  const range = document.createRange();
  range.selectNodeContents(element);
  const width = range.getBoundingClientRect().width;
  range.detach();
  return width || element.scrollWidth;
}

/**
 * Renders text that automatically scrolls horizontally when it overflows its
 * container.  When the text fits, it renders a plain element with no animation.
 *
 * Overflow is rechecked on every resize via ResizeObserver so it reacts to
 * layout changes (e.g. header collapse driven by --collapse CSS variable).
 */
export default function MarqueeText({
  text,
  as: Tag = 'span',
  className,
  style,
  cycleDuration = DEFAULT_CYCLE,
  gap = DEFAULT_GAP,
  syncDistance,
  overflowInset = 0,
  onMeasure,
}: MarqueeTextProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const measureRef = useRef<HTMLElement>(null);
  const [overflows, setOverflows] = useState(false);
  const [textWidth, setTextWidth] = useState(0);
  const phaseRef = useRef<{ key: string; delay: number } | null>(null);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const check = () => {
      const measure = measureRef.current;
      if (!measure) return;
      const isTrackRender = container.firstElementChild instanceof HTMLElement && container.firstElementChild.classList.contains(cls.track);
      const renderedTextWidth = isTrackRender ? 0 : container.scrollWidth;
      const measuredTextWidth = getTextWidth(measure);
      const tw = Math.max(measuredTextWidth, renderedTextWidth);
      const availableWidth = container.clientWidth;
      const isRenderedClipped = !isTrackRender && renderedTextWidth > availableWidth;
      const isNearEdge = overflowInset > 0 && measuredTextWidth > availableWidth - overflowInset;
      const nowOverflows = isRenderedClipped || isNearEdge || measuredTextWidth > availableWidth + 1;
      setOverflows(prev => prev === nowOverflows ? prev : nowOverflows);
      setTextWidth(prev => prev === tw ? prev : tw);
      onMeasure?.(nowOverflows ? tw : 0);
    };

    const ro = new ResizeObserver(check);
    ro.observe(container);
    check();
    const frame = window.requestAnimationFrame(check);

    return () => {
      window.cancelAnimationFrame(frame);
      ro.disconnect();
    };
  }, [text, cycleDuration, gap, overflowInset, onMeasure]);

  /** Reset UA styles so the inner element inherits font sizing from the container. */
  const tagReset: CSSProperties = { margin: 0, fontSize: 'inherit', fontWeight: 'inherit' };

  // When not overflowing, render a plain semantic element.
  if (!overflows) {
    return (
      <div ref={containerRef} className={className} style={{ ...style, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        {createElement(Tag, { ref: measureRef, style: tagReset }, text)}
      </div>
    );
  }

  // Compute translate distance and gap for this instance.
  const translate = Math.round(syncDistance ?? (textWidth + gap));
  const adjustedGap = Math.max(0, translate - textWidth);
  // Fixed cycle duration ensures all instances share the same period,
  // so epoch-based animation-delay produces perfect cross-card alignment.
  const duration = cycleDuration;

  // Compute negative animation-delay for cross-card phase alignment.
  const phaseKey = `${text}|${duration}|${translate}`;
  if (phaseRef.current?.key !== phaseKey) {
    const elapsed = (Date.now() - MARQUEE_EPOCH) / 1000;
    phaseRef.current = { key: phaseKey, delay: -(elapsed % duration) };
  }
  const animDelay = phaseRef.current.delay;

  const trackStyle: CSSProperties = {
    '--marquee-duration': `${duration.toFixed(2)}s`,
    '--marquee-gap': `${adjustedGap}px`,
    '--marquee-translate': `${-translate}px`,
    '--marquee-delay': `${animDelay.toFixed(2)}s`,
  } as CSSProperties;
  const tagResetShrink: CSSProperties = { ...tagReset, flexShrink: 0 };

  return (
    <div
      ref={containerRef}
      className={`${cls.container}${className ? ` ${className}` : ''}`}
      style={style}
    >
      <div className={cls.track} style={trackStyle}>
        {createElement(Tag, { ref: measureRef, style: tagResetShrink }, text)}
        {createElement(Tag, { 'aria-hidden': true, style: tagResetShrink } as Record<string, unknown>, text)}
      </div>
    </div>
  );
}
