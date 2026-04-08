import { useRef, useState, useEffect, type ReactNode } from 'react';

const COLLAPSE_MS = 300;

interface CollapseOnExitProps {
  /** When false, content fades out and the container height collapses to zero. */
  show: boolean;
  children: ReactNode;
  /** Called after the collapse animation finishes. */
  onCollapsed?: () => void;
}

/**
 * Keeps children mounted during an exit animation: first fading opacity,
 * then smoothly collapsing the container height to zero.  Once the collapse
 * completes the component unmounts its subtree and notifies the parent via
 * `onCollapsed`.
 */
export default function CollapseOnExit({ show, children, onCollapsed }: CollapseOnExitProps) {
  const ref = useRef<HTMLDivElement>(null);
  const lastChildrenRef = useRef<ReactNode>(children);
  const [mounted, setMounted] = useState(show);

  // Cache the last non-null children so they remain visible during collapse
  if (show && children != null) lastChildrenRef.current = children;

  useEffect(() => {
    if (show) {
      setMounted(true);
      const el = ref.current;
      if (el) {
        el.style.height = '';
        el.style.overflow = '';
        el.style.transition = '';
        el.style.opacity = '';
      }
      return;
    }

    // show went false — collapse height
    const el = ref.current;
    if (!el) {
      setMounted(false);
      onCollapsed?.();
      return;
    }

    // Pin current height then transition to zero
    const h = el.scrollHeight;
    el.style.overflow = 'hidden';
    el.style.height = `${h}px`;
    void el.offsetHeight; // force reflow so browser captures start value

    el.style.transition = `height ${COLLAPSE_MS}ms ease-out, opacity ${COLLAPSE_MS / 2}ms ease-out`;
    el.style.opacity = '0';
    el.style.height = '0';

    const finish = () => {
      el.removeEventListener('transitionend', onEnd);
      clearTimeout(safety);
      setMounted(false);
      onCollapsed?.();
    };

    const onEnd = (e: Event) => {
      if ((e as TransitionEvent).propertyName !== 'height') return;
      finish();
    };

    el.addEventListener('transitionend', onEnd);
    const safety = setTimeout(finish, COLLAPSE_MS + 50);

    return () => {
      el.removeEventListener('transitionend', onEnd);
      clearTimeout(safety);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only react to show changes
  }, [show]);

  if (!mounted) return null;

  return <div ref={ref}>{lastChildrenRef.current}</div>;
}
