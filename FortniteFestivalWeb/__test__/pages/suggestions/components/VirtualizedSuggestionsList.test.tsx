import { act, cleanup, render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ComponentProps, ReactNode } from 'react';
import type { SuggestionCategory } from '@festival/core/types';
import { LoadPhase } from '@festival/core/runtime';

type VirtualizerOptions = {
  count: number;
  getScrollElement: () => HTMLElement | null;
  getItemKey: (index: number) => string | number;
};

type MockVirtualizer = {
  options: VirtualizerOptions;
  measure: ReturnType<typeof vi.fn>;
  measureElement: ReturnType<typeof vi.fn>;
  getVirtualItems: ReturnType<typeof vi.fn>;
  getTotalSize: ReturnType<typeof vi.fn>;
  shouldAdjustScrollPositionOnItemSizeChange?: () => boolean;
};

let virtualizer: MockVirtualizer;
let cancelAnimationFrameMock: ReturnType<typeof vi.fn>;

vi.mock('@tanstack/react-virtual', () => ({
  defaultRangeExtractor: ({ start, end }: { start: number; end: number }) => (
    Array.from({ length: end - start + 1 }, (_, index) => start + index)
  ),
  useVirtualizer: (options: VirtualizerOptions) => {
    if (!virtualizer) {
      virtualizer = {
        options,
        measure: vi.fn(() => {
          requestAnimationFrame(() => {
            const scrollElement = virtualizer.options.getScrollElement();
            if (
              virtualizer.shouldAdjustScrollPositionOnItemSizeChange?.() !== false
              && scrollElement
            ) {
              scrollElement.scrollTo(0, 37);
            }
          });
        }),
        measureElement: vi.fn(),
        getVirtualItems: vi.fn(() => virtualizer.options.count > 0
          ? [{ index: 0, start: 0 }]
          : []),
        getTotalSize: vi.fn(() => 240),
      };
    }
    virtualizer.options = options;
    return virtualizer;
  },
}));

vi.mock('../../../../src/components/page/FadeIn', () => ({
  default: ({ children }: { children: ReactNode }) => children,
}));

vi.mock('../../../../src/pages/suggestions/components/CategoryCard', () => ({
  CategoryCard: () => <div data-testid="category-card" />,
}));

vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({
  useIsNarrow: () => false,
}));

vi.mock('../../../../src/hooks/ui/useScrollFade', () => ({
  useScrollFade: vi.fn(),
}));

vi.mock('../../../../src/hooks/ui/useVirtualListScrollMargin', () => ({
  useVirtualListScrollMargin: () => 0,
}));

vi.mock('../../../../src/utils/scrollViewport', () => ({
  observeScrollViewportRect: vi.fn(),
}));

const { VirtualizedSuggestionsList } = await import(
  '../../../../src/pages/suggestions/components/VirtualizedSuggestionsList'
);

const queuedFrames = new Map<number, FrameRequestCallback>();
let nextFrameId = 0;

describe('VirtualizedSuggestionsList reset window', () => {
  beforeEach(() => {
    virtualizer = undefined!;
    queuedFrames.clear();
    nextFrameId = 0;
    cancelAnimationFrameMock = vi.fn((frameId: number) => {
      queuedFrames.delete(frameId);
    });
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
      const frameId = ++nextFrameId;
      queuedFrames.set(frameId, callback);
      return frameId;
    });
    vi.stubGlobal('cancelAnimationFrame', cancelAnimationFrameMock);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it('keeps the final position at zero for measured identity-only and rapid fresh-mix changes', () => {
    const scrollElement = createScrollElement();
    const { rerender } = render(
      <VirtualizedSuggestionsList {...propsFor(scrollElement, 'mix-a')} />,
    );

    flushFrames();
    expect(scrollElement.scrollTop).toBe(37);
    expect(virtualizer.measureElement).toHaveBeenCalled();

    act(() => {
      rerender(<VirtualizedSuggestionsList {...propsFor(scrollElement, 'mix-b')} />);
    });
    expect(queuedFrames.size).toBeGreaterThan(0);

    act(() => {
      rerender(<VirtualizedSuggestionsList {...propsFor(scrollElement, 'mix-c')} />);
    });
    flushFrames();

    expect(virtualizer.measure).toHaveBeenCalled();
    expect(scrollElement.scrollTop).toBe(0);
  });

  it('cancels pending reset frames when unmounted', () => {
    const scrollElement = createScrollElement();
    const { rerender, unmount } = render(
      <VirtualizedSuggestionsList {...propsFor(scrollElement, 'mix-a')} />,
    );
    flushFrames();

    act(() => {
      rerender(<VirtualizedSuggestionsList {...propsFor(scrollElement, 'mix-b')} />);
    });
    const pendingFramesBeforeUnmount = queuedFrames.size;

    unmount();
    const scrollTopAfterUnmount = scrollElement.scrollTop;

    expect(cancelAnimationFrameMock).toHaveBeenCalled();
    expect(queuedFrames.size).toBeLessThan(pendingFramesBeforeUnmount);
    flushFrames();
    expect(scrollElement.scrollTop).toBe(scrollTopAfterUnmount);
  });
});

function createScrollElement(): HTMLElement & { scrollTo: (x: number, y: number) => void } {
  const element = document.createElement('div') as HTMLElement & {
    scrollTo: (x: number, y: number) => void;
  };
  Object.defineProperty(element, 'scrollTo', {
    value: (_x: number, y: number) => {
      element.scrollTop = y;
    },
  });
  return element;
}

function flushFrames(): void {
  let safety = 0;
  while (queuedFrames.size > 0) {
    const [frameId, callback] = queuedFrames.entries().next().value as [number, FrameRequestCallback];
    queuedFrames.delete(frameId);
    callback(0);
    safety += 1;
    if (safety > 20) throw new Error('Animation-frame queue did not settle');
  }
}

function propsFor(
  scrollElement: HTMLElement,
  identity: string,
): ComponentProps<typeof VirtualizedSuggestionsList> {
  const category: SuggestionCategory = {
    key: 'near_fc_guitar',
    title: 'Near full combo',
    description: 'Test category',
    songs: [],
  };
  return {
    rows: [{ id: 'row-1', sourceIndex: 0, category }],
    phase: LoadPhase.ContentIn,
    skipAnimation: true,
    revealedCount: 1,
    identity,
    measurementKey: 'stable-measurement',
    categoryLimit: 1_000,
    generatedCategoryCount: 1,
    loadTriggerCount: 0,
    scrollContainerRef: { current: scrollElement },
    albumArtMap: new Map(),
    scoresIndex: {},
  };
}
