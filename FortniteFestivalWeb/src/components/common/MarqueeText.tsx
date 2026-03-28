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
  /** Scroll speed in pixels per second (default 38). */
  speed?: number;
  /** Gap in pixels between the two text copies (default 28). */
  gap?: number;
}

const DEFAULT_SPEED = 38;
const DEFAULT_GAP = 28;
/** Fraction of the animation keyframe spent scrolling (rest is pause). */
const SCROLL_FRACTION = 0.9;

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
  speed = DEFAULT_SPEED,
  gap = DEFAULT_GAP,
}: MarqueeTextProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const measureRef = useRef<HTMLElement>(null);
  const [overflows, setOverflows] = useState(false);
  const [duration, setDuration] = useState(6);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const check = () => {
      // Measure the unconstrained text width from the hidden measurement element,
      // then compare against the visible container width.
      const measure = measureRef.current;
      if (!measure) return;
      const textWidth = measure.scrollWidth;
      const availableWidth = container.clientWidth;
      const nowOverflows = textWidth > availableWidth + 1;
      setOverflows(nowOverflows);
      if (nowOverflows) {
        const distance = textWidth + gap;
        setDuration(distance / Math.max(10, speed) / SCROLL_FRACTION);
      }
    };

    const ro = new ResizeObserver(check);
    ro.observe(container);

    return () => ro.disconnect();
  }, [text, speed, gap]);

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

  // When overflowing, render the scrolling track with two copies.
  const trackStyle: CSSProperties = {
    '--marquee-duration': `${duration.toFixed(2)}s`,
    '--marquee-gap': `${gap}px`,
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
