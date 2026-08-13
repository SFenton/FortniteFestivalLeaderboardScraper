import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SuggestionsLoadSentinel } from '../../../../src/pages/suggestions/components/SuggestionsLoadSentinel';

describe('SuggestionsLoadSentinel', () => {
  let callback: IntersectionObserverCallback;
  let options: IntersectionObserverInit | undefined;
  const disconnect = vi.fn();
  const observe = vi.fn();

  beforeEach(() => {
    disconnect.mockClear();
    observe.mockClear();
    class MockIntersectionObserver {
      constructor(nextCallback: IntersectionObserverCallback, nextOptions?: IntersectionObserverInit) {
        callback = nextCallback;
        options = nextOptions;
      }
      observe = observe;
      unobserve = vi.fn();
      disconnect = disconnect;
      takeRecords = vi.fn(() => []);
      root = null;
      rootMargin = '';
      thresholds = [];
    }
    vi.stubGlobal('IntersectionObserver', MockIntersectionObserver);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('observes against the application scroll root with the configured prefetch margin', () => {
    const root = document.createElement('div');
    const rootRef = { current: root };
    const onLoadMore = vi.fn();
    const { rerender } = render(
      <SuggestionsLoadSentinel
        rootRef={rootRef}
        disabled={false}
        triggerKey={10}
        prefetchPx={600}
        onLoadMore={onLoadMore}
        fallbackLabel="Load more"
      />,
    );
    const sentinel = screen.getByTestId('suggestions-load-sentinel');
    expect(observe).toHaveBeenCalledWith(sentinel);
    expect(options).toMatchObject({
      root,
      rootMargin: '0px 0px 600px 0px',
      threshold: 0,
    });

    act(() => {
      callback([
        intersectingEntry(sentinel),
      ], {} as IntersectionObserver);
      callback([
        intersectingEntry(sentinel),
      ], {} as IntersectionObserver);
    });
    expect(onLoadMore).toHaveBeenCalledTimes(1);

    rerender(
      <SuggestionsLoadSentinel
        rootRef={rootRef}
        disabled={false}
        triggerKey={16}
        prefetchPx={600}
        onLoadMore={onLoadMore}
        fallbackLabel="Load more"
      />,
    );
    act(() => {
      callback([
        intersectingEntry(sentinel),
      ], {} as IntersectionObserver);
    });
    expect(onLoadMore).toHaveBeenCalledTimes(2);
    expect(disconnect).toHaveBeenCalled();
  });

  it('provides a manual fallback when IntersectionObserver is unavailable', () => {
    vi.stubGlobal('IntersectionObserver', undefined);
    const onLoadMore = vi.fn();
    render(
      <SuggestionsLoadSentinel
        rootRef={{ current: document.createElement('div') }}
        disabled={false}
        triggerKey={0}
        prefetchPx={600}
        onLoadMore={onLoadMore}
        fallbackLabel="Load more suggestions"
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Load more suggestions' }));
    expect(onLoadMore).toHaveBeenCalledTimes(1);
  });
});

function intersectingEntry(target: Element): IntersectionObserverEntry {
  const rect = target.getBoundingClientRect();
  return {
    target,
    isIntersecting: true,
    intersectionRatio: 1,
    boundingClientRect: rect,
    intersectionRect: rect,
    rootBounds: null,
    time: 0,
  };
}
