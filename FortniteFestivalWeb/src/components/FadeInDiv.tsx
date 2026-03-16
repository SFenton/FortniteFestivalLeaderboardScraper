import { useRef, useCallback, memo, type CSSProperties, type ReactNode } from 'react';
import { FADE_DURATION } from '@festival/theme';
import css from './FadeInDiv.module.css';

interface FadeInDivProps {
  /** Stagger delay in ms.  `undefined` → render children without animation. */
  delay?: number;
  /** When true, render the wrapper at opacity 0 (hidden but in the DOM). */
  hidden?: boolean;
  children: ReactNode;
  style?: CSSProperties;
}

/**
 * Wrapper that fades children in with a `fadeInUp` animation.
 * Cleans up inline animation styles after completion so they don't interfere
 * with subsequent re-renders or `useStaggerRush`.
 */
const FadeInDiv = memo(function FadeInDiv({ delay, hidden, children, style }: FadeInDivProps) {
  const ref = useRef<HTMLDivElement>(null);
  const handleEnd = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);

  if (delay == null) return <div style={style}>{children}</div>;

  if (hidden) return <div className={css.hidden} style={style}>{children}</div>;

  return (
    <div
      ref={ref}
      className={css.wrapper}
      style={{ '--fade-animation': `fadeInUp ${FADE_DURATION}ms ease-out ${delay}ms forwards`, ...style } as CSSProperties}
      onAnimationEnd={handleEnd}
    >
      {children}
    </div>
  );
});

export default FadeInDiv;
