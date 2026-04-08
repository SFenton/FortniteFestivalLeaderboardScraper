import { useCallback, useRef, useMemo, memo, type CSSProperties, type ReactNode, type ElementType, type ComponentPropsWithRef } from 'react';
import { Opacity, FADE_DURATION } from '@festival/theme';

type FadeInOwnProps<T extends ElementType = 'div'> = {
  /** Render as a different element type (e.g. Link, 'a', 'span'). Default: 'div'. */
  as?: T;
  /** Stagger delay in ms.  `undefined` → render children without animation. */
  delay?: number;
  /** When true, render the wrapper at opacity 0 (hidden but in the DOM). */
  hidden?: boolean;
  children: ReactNode;
  style?: CSSProperties;
  className?: string;
};

export type FadeInProps<T extends ElementType = 'div'> =
  FadeInOwnProps<T> & Omit<ComponentPropsWithRef<T>, keyof FadeInOwnProps<T>>;

/**
 * Polymorphic wrapper that fades children in with a `fadeInUp` animation.
 * Cleans up inline animation styles after completion so they don't interfere
 * with subsequent re-renders or `useStaggerRush`.
 *
 * Renders as `<div>` by default. Use the `as` prop for other element types:
 *   <FadeIn as={Link} to="/path" delay={100}>...</FadeIn>
 */
function FadeInInner<T extends ElementType = 'div'>({
  as,
  delay,
  hidden,
  children,
  style,
  className,
  ...rest
}: FadeInProps<T>) {
  // Once the entry animation has completed, lock out further animations so
  // that a delay-prop drift (e.g. an earlier sibling unmounting and shifting
  // indices) doesn't cause a visible re-stagger.
  const animatedRef = useRef(false);

  /* v8 ignore start — animation cleanup */
  const handleEnd = useCallback((e: { currentTarget: unknown }) => {
    const el = e.currentTarget as HTMLElement;
    el.style.opacity = '';
    el.style.animation = '';
    animatedRef.current = true;
    /* v8 ignore stop */
  }, []);

  const effectiveDelay = animatedRef.current ? undefined : delay;
  const s = useStyles(effectiveDelay, hidden);
  const Component: ElementType = as ?? 'div';

  if (effectiveDelay == null) return <Component className={className} style={style} {...rest}>{children}</Component>;

  return (
    <Component
      className={className}
      style={{ ...s.wrapper, ...style }}
      onAnimationEnd={hidden ? undefined : handleEnd}
      {...rest}
    >
      {children}
    </Component>
  );
}

const FadeIn = memo(FadeInInner) as typeof FadeInInner;
export default FadeIn;

function useStyles(delay: number | undefined, hidden: boolean | undefined) {
  return useMemo(() => ({
    wrapper: delay == null
      ? undefined
      : hidden
        ? { opacity: Opacity.none } as CSSProperties
        : { opacity: Opacity.none, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${delay}ms forwards` } as CSSProperties,
  }), [delay, hidden]);
}
