import { useEffect, useRef, type CSSProperties, type RefObject } from 'react';
import { Colors, Font, Gap, Radius, CssValue, padding } from '@festival/theme';

type SuggestionsLoadSentinelProps = {
  rootRef: RefObject<HTMLElement | null>;
  disabled: boolean;
  triggerKey: number;
  prefetchPx: number;
  onLoadMore: () => void;
  fallbackLabel: string;
};

export function SuggestionsLoadSentinel({
  rootRef,
  disabled,
  triggerKey,
  prefetchPx,
  onLoadMore,
  fallbackLabel,
}: SuggestionsLoadSentinelProps) {
  const sentinelRef = useRef<HTMLDivElement>(null);
  const pendingRef = useRef(false);
  const supportsObserver = typeof IntersectionObserver !== 'undefined';

  useEffect(() => {
    pendingRef.current = false;
  }, [disabled, triggerKey]);

  useEffect(() => {
    if (disabled || !supportsObserver) return;
    const target = sentinelRef.current;
    const root = rootRef.current;
    if (!target || !root) return;

    const observer = new IntersectionObserver((entries) => {
      if (pendingRef.current) return;
      if (!entries.some(entry => entry.isIntersecting || entry.intersectionRatio > 0)) return;
      pendingRef.current = true;
      onLoadMore();
    }, {
      root,
      rootMargin: `0px 0px ${prefetchPx}px 0px`,
      threshold: 0,
    });
    observer.observe(target);
    target.dataset.observerReady = 'true';
    return () => {
      target.dataset.observerReady = 'false';
      observer.disconnect();
    };
  }, [disabled, onLoadMore, prefetchPx, rootRef, supportsObserver, triggerKey]);

  return (
    <div
      ref={sentinelRef}
      data-testid="suggestions-load-sentinel"
      data-observer-ready={supportsObserver ? 'false' : 'fallback'}
      aria-hidden={supportsObserver ? 'true' : undefined}
      style={styles.sentinel}
    >
      {!disabled && !supportsObserver && (
        <button type="button" style={styles.fallbackButton} onClick={onLoadMore}>
          {fallbackLabel}
        </button>
      )}
    </div>
  );
}

const styles = {
  sentinel: {
    width: CssValue.full,
    minHeight: 1,
  } as CSSProperties,
  fallbackButton: {
    display: 'block',
    margin: `${Gap.md}px auto`,
    padding: padding(Gap.md, Gap.xl),
    border: 0,
    borderRadius: Radius.full,
    backgroundColor: Colors.surfaceElevated,
    color: Colors.textPrimary,
    fontSize: Font.md,
    cursor: 'pointer',
  } as CSSProperties,
};
