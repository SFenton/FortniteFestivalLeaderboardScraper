import { act, fireEvent, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DEFAULT_QUICK_LINK_SCROLL_OFFSET, usePageQuickLinks, type PageQuickLinkItem } from '../../../src/hooks/ui/usePageQuickLinks';

function createScrollContainer({ clientHeight, scrollHeight }: { clientHeight: number; scrollHeight: number; }) {
  const scrollEl = document.createElement('div');
  Object.defineProperty(scrollEl, 'clientHeight', { value: clientHeight, writable: true, configurable: true });
  Object.defineProperty(scrollEl, 'scrollHeight', { value: scrollHeight, writable: true, configurable: true });
  Object.defineProperty(scrollEl, 'scrollTop', { value: 0, writable: true, configurable: true });
  Object.defineProperty(scrollEl, 'getBoundingClientRect', {
    configurable: true,
    value: () => ({
      top: 0,
      left: 0,
      bottom: clientHeight,
      right: 600,
      width: 600,
      height: clientHeight,
      x: 0,
      y: 0,
      toJSON() { return this; },
    }),
  });
  scrollEl.scrollTo = vi.fn(({ top }: ScrollToOptions) => {
    const maxScrollTop = Math.max(0, scrollHeight - clientHeight);
    scrollEl.scrollTop = Math.min(Math.max(top ?? 0, 0), maxScrollTop);
  }) as typeof scrollEl.scrollTo;
  return scrollEl;
}

function createSection(scrollEl: HTMLElement, absoluteTop: number, height = 60) {
  const sectionEl = document.createElement('section');
  Object.defineProperty(sectionEl, 'getBoundingClientRect', {
    configurable: true,
    value: () => ({
      top: absoluteTop - scrollEl.scrollTop,
      left: 0,
      bottom: absoluteTop - scrollEl.scrollTop + height,
      right: 600,
      width: 600,
      height,
      x: 0,
      y: absoluteTop - scrollEl.scrollTop,
      toJSON() { return this; },
    }),
  });
  return sectionEl;
}

function dispatchScroll(scrollEl: HTMLElement, nextTop: number) {
  scrollEl.scrollTop = nextTop;
  fireEvent.scroll(scrollEl);
}

describe('usePageQuickLinks', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('keeps a bottom-boundary quick link active while its section remains visible', async () => {
    const scrollEl = createScrollContainer({ clientHeight: 540, scrollHeight: 1000 });
    const scrollContainerRef = { current: scrollEl };
    const items: readonly PageQuickLinkItem[] = [
      { id: 'y', label: 'Y', landmarkLabel: 'Y' },
      { id: 'z', label: 'Z', landmarkLabel: 'Z' },
    ];

    const { result } = renderHook(() => usePageQuickLinks({
      items,
      scrollContainerRef,
      isDesktopRailEnabled: true,
    }));

    const ySection = createSection(scrollEl, 400);
    const zSection = createSection(scrollEl, 700);

    act(() => {
      result.current.registerSectionRef('y', ySection);
      result.current.registerSectionRef('z', zSection);
      fireEvent.scroll(scrollEl);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('y');
    });

    act(() => {
      result.current.handleQuickLinkSelect(items[1]!);
    });

    expect(scrollEl.scrollTop).toBe(460);

    act(() => {
      fireEvent.scroll(scrollEl);
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120);
    });

    expect(result.current.activeItemId).toBe('z');

    act(() => {
      dispatchScroll(scrollEl, 420);
    });

    expect(result.current.activeItemId).toBe('z');

    act(() => {
      dispatchScroll(scrollEl, 100);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('y');
    });
  });

  it('does not pin a reachable quick link after scrolling away from it', async () => {
    const scrollEl = createScrollContainer({ clientHeight: 540, scrollHeight: 2000 });
    const scrollContainerRef = { current: scrollEl };
    const items: readonly PageQuickLinkItem[] = [
      { id: 'global', label: 'Global', landmarkLabel: 'Global' },
      { id: 'top-songs', label: 'Top Songs', landmarkLabel: 'Top Songs' },
    ];

    const { result } = renderHook(() => usePageQuickLinks({
      items,
      scrollContainerRef,
      isDesktopRailEnabled: true,
    }));

    const globalSection = createSection(scrollEl, 80);
    const topSongsSection = createSection(scrollEl, 980);

    act(() => {
      result.current.registerSectionRef('global', globalSection);
      result.current.registerSectionRef('top-songs', topSongsSection);
      fireEvent.scroll(scrollEl);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('global');
    });

    act(() => {
      result.current.handleQuickLinkSelect(items[1]!);
    });

    expect(scrollEl.scrollTop).toBe(980 - DEFAULT_QUICK_LINK_SCROLL_OFFSET);

    act(() => {
      fireEvent.scroll(scrollEl);
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120);
    });

    expect(result.current.activeItemId).toBe('top-songs');

    act(() => {
      dispatchScroll(scrollEl, 980 - DEFAULT_QUICK_LINK_SCROLL_OFFSET - 4);
    });

    expect(result.current.activeItemId).toBe('top-songs');

    await act(async () => {
      await vi.advanceTimersByTimeAsync(250);
    });

    act(() => {
      dispatchScroll(scrollEl, 600);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('global');
    });
  });

  it('keeps the previous bottom-boundary highlight during reverse scroll and switches directly to the clicked target', async () => {
    const scrollEl = createScrollContainer({ clientHeight: 540, scrollHeight: 1200 });
    const scrollContainerRef = { current: scrollEl };
    const items: readonly PageQuickLinkItem[] = [
      { id: 'b', label: 'B', landmarkLabel: 'B' },
      { id: 'c', label: 'C', landmarkLabel: 'C' },
      { id: 'z', label: 'Z', landmarkLabel: 'Z' },
    ];

    const { result } = renderHook(() => usePageQuickLinks({
      items,
      scrollContainerRef,
      isDesktopRailEnabled: true,
    }));

    const bSection = createSection(scrollEl, 100);
    const cSection = createSection(scrollEl, 400);
    const zSection = createSection(scrollEl, 900);

    act(() => {
      result.current.registerSectionRef('b', bSection);
      result.current.registerSectionRef('c', cSection);
      result.current.registerSectionRef('z', zSection);
      fireEvent.scroll(scrollEl);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('b');
    });
    act(() => {
      result.current.handleQuickLinkSelect(items[2]!);
    });

    expect(scrollEl.scrollTop).toBe(660);

    act(() => {
      fireEvent.scroll(scrollEl);
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120);
    });

    expect(result.current.activeItemId).toBe('z');

    act(() => {
      result.current.handleQuickLinkSelect(items[1]!);
    });

    expect(result.current.activeItemId).toBe('z');

    act(() => {
      dispatchScroll(scrollEl, 320);
    });

    expect(result.current.activeItemId).toBe('z');

    act(() => {
      dispatchScroll(scrollEl, 400 - DEFAULT_QUICK_LINK_SCROLL_OFFSET);
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120);
    });

    expect(result.current.activeItemId).toBe('c');
    expect(result.current.activeItemId).not.toBe('b');

    act(() => {
      dispatchScroll(scrollEl, 400 - DEFAULT_QUICK_LINK_SCROLL_OFFSET - 4);
    });

    expect(result.current.activeItemId).toBe('c');

    await act(async () => {
      await vi.advanceTimersByTimeAsync(250);
    });

    act(() => {
      dispatchScroll(scrollEl, 240);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('b');
    });
  });

  it('keeps a clicked reachable target active through the post-settle top-zone without falling back to the previous section', async () => {
    const scrollEl = createScrollContainer({ clientHeight: 540, scrollHeight: 2000 });
    const scrollContainerRef = { current: scrollEl };
    const items: readonly PageQuickLinkItem[] = [
      { id: 'o', label: 'O', landmarkLabel: 'O' },
      { id: 'p', label: 'P', landmarkLabel: 'P' },
      { id: 'v', label: 'V', landmarkLabel: 'V' },
    ];

    const { result } = renderHook(() => usePageQuickLinks({
      items,
      scrollContainerRef,
      isDesktopRailEnabled: true,
    }));

    const oSection = createSection(scrollEl, 100);
    const pSection = createSection(scrollEl, 400);
    const vSection = createSection(scrollEl, 1000);

    act(() => {
      result.current.registerSectionRef('o', oSection);
      result.current.registerSectionRef('p', pSection);
      result.current.registerSectionRef('v', vSection);
      fireEvent.scroll(scrollEl);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('o');
    });

    act(() => {
      result.current.handleQuickLinkSelect(items[2]!);
      fireEvent.scroll(scrollEl);
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120);
    });

    expect(result.current.activeItemId).toBe('v');

    act(() => {
      result.current.handleQuickLinkSelect(items[1]!);
    });

    expect(result.current.activeItemId).toBe('v');

    act(() => {
      dispatchScroll(scrollEl, 400 - DEFAULT_QUICK_LINK_SCROLL_OFFSET);
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120);
    });

    expect(result.current.activeItemId).toBe('p');

    act(() => {
      dispatchScroll(scrollEl, 340);
    });

    expect(result.current.activeItemId).toBe('p');
    expect(result.current.activeItemId).not.toBe('o');

    await act(async () => {
      await vi.advanceTimersByTimeAsync(250);
    });

    act(() => {
      dispatchScroll(scrollEl, 240);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('o');
    });
  });

  it('keeps a freshly locked reachable target active through the early post-lock handoff without flashing the previous section', async () => {
    const scrollEl = createScrollContainer({ clientHeight: 540, scrollHeight: 2500 });
    const scrollContainerRef = { current: scrollEl };
    const items: readonly PageQuickLinkItem[] = [
      { id: 'numeric', label: '#', landmarkLabel: '#' },
      { id: 'i', label: 'I', landmarkLabel: 'I' },
      { id: 'j', label: 'J', landmarkLabel: 'J' },
    ];
    const estimatedTopById = new Map<string, number>([
      ['numeric', 0],
      ['i', 880],
      ['j', 1000],
    ]);

    const { result } = renderHook(() => usePageQuickLinks({
      items,
      scrollContainerRef,
      isDesktopRailEnabled: true,
      getItemTop: (id) => estimatedTopById.get(id) ?? null,
    }));

    act(() => {
      fireEvent.scroll(scrollEl);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('numeric');
    });

    act(() => {
      scrollEl.scrollTop = 1000 - DEFAULT_QUICK_LINK_SCROLL_OFFSET;
      result.current.handleQuickLinkSelect(items[2]!);
    });

    expect(result.current.activeItemId).toBe('j');

    const numericSection = createSection(scrollEl, 0);
    const iSection = createSection(scrollEl, 900);
    const jSection = createSection(scrollEl, 1120);

    act(() => {
      result.current.registerSectionRef('numeric', numericSection);
      result.current.registerSectionRef('i', iSection);
      result.current.registerSectionRef('j', jSection);
      fireEvent.scroll(scrollEl);
    });

    expect(result.current.activeItemId).toBe('j');
    expect(result.current.activeItemId).not.toBe('i');

    act(() => {
      dispatchScroll(scrollEl, 930);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('i');
    });
  });

  it('keeps an adjacent reachable target active after arrival instead of flashing its previous section', async () => {
    const scrollEl = createScrollContainer({ clientHeight: 540, scrollHeight: 2200 });
    const scrollContainerRef = { current: scrollEl };
    const items: readonly PageQuickLinkItem[] = [
      { id: 'c', label: 'C', landmarkLabel: 'C' },
      { id: 'e', label: 'E', landmarkLabel: 'E' },
      { id: 'f', label: 'F', landmarkLabel: 'F' },
    ];

    const { result } = renderHook(() => usePageQuickLinks({
      items,
      scrollContainerRef,
      isDesktopRailEnabled: true,
    }));

    const cSection = createSection(scrollEl, 100);
    const eSection = createSection(scrollEl, 740);
    const fSection = createSection(scrollEl, 980);

    act(() => {
      result.current.registerSectionRef('c', cSection);
      result.current.registerSectionRef('e', eSection);
      result.current.registerSectionRef('f', fSection);
      fireEvent.scroll(scrollEl);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('c');
    });

    act(() => {
      result.current.handleQuickLinkSelect(items[2]!);
    });

    expect(result.current.activeItemId).toBe('c');
    expect(scrollEl.scrollTop).toBe(980 - DEFAULT_QUICK_LINK_SCROLL_OFFSET);

    act(() => {
      fireEvent.scroll(scrollEl);
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120);
    });

    expect(result.current.activeItemId).toBe('f');

    act(() => {
      dispatchScroll(scrollEl, 930);
    });

    expect(result.current.activeItemId).toBe('f');
    expect(result.current.activeItemId).not.toBe('e');

    act(() => {
      dispatchScroll(scrollEl, 820);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('e');
    });
  });

  it('holds the previous quick link immediately when selection intent is announced before an external scroll controller moves the page', async () => {
    const scrollEl = createScrollContainer({ clientHeight: 540, scrollHeight: 2500 });
    const scrollContainerRef = { current: scrollEl };
    const items: readonly PageQuickLinkItem[] = [
      { id: 'numeric', label: '#', landmarkLabel: '#' },
      { id: 'i', label: 'I', landmarkLabel: 'I' },
      { id: 'j', label: 'J', landmarkLabel: 'J' },
    ];
    const estimatedTopById = new Map<string, number>([
      ['numeric', 0],
      ['i', 900],
      ['j', 1120],
    ]);

    const { result } = renderHook(() => usePageQuickLinks({
      items,
      scrollContainerRef,
      isDesktopRailEnabled: true,
      getItemTop: (id) => estimatedTopById.get(id) ?? null,
    }));

    act(() => {
      fireEvent.scroll(scrollEl);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('numeric');
    });

    act(() => {
      scrollEl.scrollTop = 900;
      fireEvent.scroll(scrollEl);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('i');
    });

    act(() => {
      result.current.handleQuickLinkSelect(items[2]!, { skipScroll: true });
    });

    expect(result.current.activeItemId).toBe('i');
    expect(scrollEl.scrollTo).not.toHaveBeenCalled();

    act(() => {
      dispatchScroll(scrollEl, 1120 - DEFAULT_QUICK_LINK_SCROLL_OFFSET);
    });

    expect(result.current.activeItemId).toBe('i');

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120);
    });

    expect(result.current.activeItemId).toBe('j');
    expect(result.current.activeItemId).not.toBe('i');
  });

  it('keeps the clicked compact quick link active while an external scroll controller moves toward it', async () => {
    const scrollEl = createScrollContainer({ clientHeight: 540, scrollHeight: 3200 });
    const scrollContainerRef = { current: scrollEl };
    const items: readonly PageQuickLinkItem[] = [
      { id: 'numeric', label: '#', landmarkLabel: '#' },
      { id: 'a', label: 'A', landmarkLabel: 'A' },
      { id: 'b', label: 'B', landmarkLabel: 'B' },
    ];

    const { result } = renderHook(() => usePageQuickLinks({
      items,
      scrollContainerRef,
      isDesktopRailEnabled: false,
    }));

    const numericSection = createSection(scrollEl, 0);
    const aSection = createSection(scrollEl, 2500);
    const bSection = createSection(scrollEl, 2628);

    act(() => {
      result.current.registerSectionRef('numeric', numericSection);
      result.current.registerSectionRef('a', aSection);
      result.current.registerSectionRef('b', bSection);
      fireEvent.scroll(scrollEl);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('numeric');
    });

    act(() => {
      result.current.handleQuickLinkSelect(items[2]!, { skipScroll: true });
    });

    expect(result.current.activeItemId).toBe('b');
    expect(scrollEl.scrollTo).not.toHaveBeenCalled();

    act(() => {
      dispatchScroll(scrollEl, 2546);
    });

    expect(result.current.activeItemId).toBe('b');
    expect(result.current.activeItemId).not.toBe('a');

    act(() => {
      dispatchScroll(scrollEl, 2628 - DEFAULT_QUICK_LINK_SCROLL_OFFSET);
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120);
    });

    expect(result.current.activeItemId).toBe('b');

    act(() => {
      dispatchScroll(scrollEl, 2470);
    });

    await waitFor(() => {
      expect(result.current.activeItemId).toBe('a');
    });
  });

  it('resyncs compact modal active state after manual scroll before reopen', () => {
    const scrollEl = createScrollContainer({ clientHeight: 540, scrollHeight: 2000 });
    const scrollContainerRef = { current: scrollEl };
    const items: readonly PageQuickLinkItem[] = [
      { id: 'global', label: 'Global', landmarkLabel: 'Global' },
      { id: 'top-songs', label: 'Top Songs', landmarkLabel: 'Top Songs' },
    ];

    const { result } = renderHook(() => usePageQuickLinks({
      items,
      scrollContainerRef,
      isDesktopRailEnabled: false,
    }));

    const globalSection = createSection(scrollEl, 80);
    const topSongsSection = createSection(scrollEl, 980);

    act(() => {
      result.current.registerSectionRef('global', globalSection);
      result.current.registerSectionRef('top-songs', topSongsSection);
      fireEvent.scroll(scrollEl);
      result.current.openQuickLinks();
    });

    expect(result.current.quickLinksOpen).toBe(true);
    expect(result.current.activeItemId).toBe('global');

    act(() => {
      result.current.closeQuickLinks();
      result.current.handleQuickLinkSelect(items[1]!);
    });

    expect(scrollEl.scrollTop).toBe(980 - DEFAULT_QUICK_LINK_SCROLL_OFFSET);
    expect(result.current.quickLinksOpen).toBe(false);
    expect(result.current.activeItemId).toBe('top-songs');

    act(() => {
      dispatchScroll(scrollEl, 80);
    });

    expect(result.current.activeItemId).toBe('global');

    act(() => {
      result.current.openQuickLinks();
    });

    expect(result.current.quickLinksOpen).toBe(true);
    expect(result.current.activeItemId).toBe('global');
  });
});