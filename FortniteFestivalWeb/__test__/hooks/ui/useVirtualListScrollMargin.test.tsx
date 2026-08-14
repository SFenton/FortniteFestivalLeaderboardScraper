import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { stubResizeObserver } from '../../helpers/browserStubs';
import { useVirtualListScrollMargin } from '../../../src/hooks/ui/useVirtualListScrollMargin';

describe('useVirtualListScrollMargin', () => {
  it('measures the list offset and resets while disabled', () => {
    const observers = stubResizeObserver();
    const scrollElement = document.createElement('div');
    const listElement = document.createElement('div');
    const outerElement = document.createElement('div');
    scrollElement.scrollTop = 120;
    scrollElement.getBoundingClientRect = () => rect(20);
    listElement.getBoundingClientRect = () => rect(80);
    const scrollContainerRef = { current: scrollElement };
    const listRef = { current: listElement };
    const outerLayoutRef = { current: outerElement };

    const { result, rerender } = renderHook(
      ({ enabled, revision }) => useVirtualListScrollMargin({
        scrollContainerRef,
        listRef,
        outerLayoutRef,
        enabled,
        revision,
      }),
      { initialProps: { enabled: true, revision: 'a' } },
    );

    expect(result.current).toBe(180);
    expect(observers.flatMap(observer => observer.targets)).toEqual(
      expect.arrayContaining([scrollElement, listElement, outerElement]),
    );

    act(() => {
      rerender({ enabled: false, revision: 'b' });
    });
    expect(result.current).toBe(0);
  });

  it('returns zero without mounted elements', () => {
    const { result } = renderHook(() => useVirtualListScrollMargin({
      scrollContainerRef: { current: null },
      listRef: { current: null },
      enabled: true,
      revision: 0,
    }));
    expect(result.current).toBe(0);
  });
});

function rect(top: number): DOMRect {
  return {
    top,
    bottom: top + 100,
    left: 0,
    right: 100,
    width: 100,
    height: 100,
    x: 0,
    y: top,
    toJSON: () => ({}),
  };
}
