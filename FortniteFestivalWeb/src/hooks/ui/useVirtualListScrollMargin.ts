import { useCallback, useEffect, useState, type RefObject } from 'react';

export function useVirtualListScrollMargin({
  scrollContainerRef,
  listRef,
  outerLayoutRef,
  enabled,
  revision,
}: {
  scrollContainerRef: RefObject<HTMLElement | null>;
  listRef: RefObject<HTMLElement | null>;
  outerLayoutRef?: RefObject<HTMLElement | null>;
  enabled: boolean;
  revision: unknown;
}): number {
  const [scrollMargin, setScrollMargin] = useState(0);

  const resolveScrollMargin = useCallback(() => {
    return resolveVirtualListScrollMargin(
      scrollContainerRef.current,
      listRef.current,
    );
  }, [listRef, scrollContainerRef]);

  useEffect(() => {
    if (!enabled) {
      setScrollMargin(0);
      return;
    }

    const update = () => {
      const nextMargin = Math.round(resolveScrollMargin());
      setScrollMargin(current => current === nextMargin ? current : nextMargin);
    };
    update();

    const resizeObserver = typeof ResizeObserver === 'undefined'
      ? null
      : new ResizeObserver(update);
    for (const element of new Set([
      outerLayoutRef?.current,
      listRef.current,
      scrollContainerRef.current,
    ])) {
      if (element) resizeObserver?.observe(element);
    }
    window.addEventListener('resize', update);

    return () => {
      resizeObserver?.disconnect();
      window.removeEventListener('resize', update);
    };
  }, [
    enabled,
    listRef,
    outerLayoutRef,
    resolveScrollMargin,
    revision,
    scrollContainerRef,
  ]);

  return scrollMargin;
}

export function resolveVirtualListScrollMargin(
  scrollElement: HTMLElement | null,
  listElement: HTMLElement | null,
): number {
  if (!scrollElement || !listElement) return 0;
  const scrollRect = scrollElement.getBoundingClientRect();
  const listRect = listElement.getBoundingClientRect();
  return Math.max(
    0,
    scrollElement.scrollTop + listRect.top - scrollRect.top,
  );
}
