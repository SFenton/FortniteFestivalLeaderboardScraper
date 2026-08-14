import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  getSuggestionsScrollRestoreState,
  initializeSuggestionsScrollState,
  markCurrentSuggestionsScrollRestorable,
  updateSuggestionsScrollY,
} from '../../../src/pages/suggestions/suggestionsSessionCache';
import { beginSuggestionsScrollRestoration } from '../../../src/pages/suggestions/suggestionsScrollRestoration';

let queuedFrame: FrameRequestCallback | null = null;
let mutationCallback: MutationCallback | null = null;

describe('Suggestions scroll restoration', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    queuedFrame = null;
    mutationCallback = null;
    vi.stubGlobal('requestAnimationFrame', vi.fn((callback: FrameRequestCallback) => {
      queuedFrame = callback;
      return 1;
    }));
    vi.stubGlobal('cancelAnimationFrame', vi.fn());
    class MockMutationObserver {
      constructor(callback: MutationCallback) {
        mutationCallback = callback;
      }
      observe() {}
      disconnect() {}
      takeRecords() { return []; }
    }
    vi.stubGlobal('MutationObserver', MockMutationObserver);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('releases a fresh pending restore on user input', () => {
    initializeSuggestionsScrollState('solo:fresh');
    const scrollElement = createScrollElement('solo:fresh');
    scrollElement.scrollTop = 500;

    const cleanup = beginSuggestionsScrollRestoration(scrollElement);
    expect(scrollElement.scrollTop).toBe(0);

    window.dispatchEvent(new WheelEvent('wheel', { deltaY: 100 }));
    scrollElement.scrollTop = 400;
    scrollElement.dispatchEvent(new Event('scroll'));
    vi.runAllTimers();
    expect(scrollElement.scrollTop).toBe(400);
    cleanup();
  });

  it('restores the immutable snapshot even if live scroll changes afterward', () => {
    initializeSuggestionsScrollState('solo:returning');
    updateSuggestionsScrollY('solo:returning', 1_200);
    markCurrentSuggestionsScrollRestorable();
    updateSuggestionsScrollY('solo:returning', 0);
    const scrollElement = createScrollElement('solo:returning');

    const cleanup = beginSuggestionsScrollRestoration(scrollElement);
    expect(scrollElement.scrollTop).toBe(1_200);
    cleanup();
  });

  it('resets a stale snapshot when a new generator is initialized for that key', () => {
    initializeSuggestionsScrollState('solo:recreated');
    updateSuggestionsScrollY('solo:recreated', 1_200);
    markCurrentSuggestionsScrollRestorable();

    initializeSuggestionsScrollState('solo:recreated');
    expect(getSuggestionsScrollRestoreState('solo:recreated')).toEqual({
      matches: true,
      restorable: false,
      scrollY: 0,
    });
  });

  it('invalidates snapshots for discarded generator identities', () => {
    initializeSuggestionsScrollState('solo:discarded');
    updateSuggestionsScrollY('solo:discarded', 1_200);
    markCurrentSuggestionsScrollRestorable();

    initializeSuggestionsScrollState('solo:other');
    expect(getSuggestionsScrollRestoreState('solo:discarded')).toEqual({
      matches: false,
      restorable: false,
      scrollY: 0,
    });
  });

  it('keeps a clamped target pending until content can represent it', () => {
    initializeSuggestionsScrollState('solo:materializing');
    updateSuggestionsScrollY('solo:materializing', 5_000);
    markCurrentSuggestionsScrollRestorable();
    const scrollElement = createScrollElement('solo:materializing', 2_000);

    const cleanup = beginSuggestionsScrollRestoration(scrollElement);
    expect(scrollElement.scrollTop).toBe(1_200);
    expect(getSuggestionsScrollRestoreState('solo:materializing').restorable).toBe(true);

    Object.defineProperty(scrollElement, 'scrollHeight', {
      configurable: true,
      value: 7_000,
    });
    mutationCallback?.([], {} as MutationObserver);
    expect(scrollElement.scrollTop).toBe(5_000);
    cleanup();
  });

  it('corrects late shifts until the restored position is stable', () => {
    initializeSuggestionsScrollState('solo:top');
    markCurrentSuggestionsScrollRestorable();
    const scrollElement = createScrollElement('solo:top');
    const cleanup = beginSuggestionsScrollRestoration(scrollElement);

    scrollElement.scrollTop = 600;
    scrollElement.dispatchEvent(new Event('scroll'));
    expect(scrollElement.scrollTop).toBe(0);

    scrollElement.scrollTop = 400;
    scrollElement.dispatchEvent(new Event('scroll'));
    expect(scrollElement.scrollTop).toBe(0);

    vi.advanceTimersByTime(1_000);
    scrollElement.scrollTop = 400;
    scrollElement.dispatchEvent(new Event('scroll'));
    expect(scrollElement.scrollTop).toBe(400);
    cleanup();
  });

  it('cancels the zero guard on user input', () => {
    initializeSuggestionsScrollState('solo:intent');
    markCurrentSuggestionsScrollRestorable();
    const scrollElement = createScrollElement('solo:intent');
    const cleanup = beginSuggestionsScrollRestoration(scrollElement);

    window.dispatchEvent(new WheelEvent('wheel', { deltaY: 100 }));
    scrollElement.scrollTop = 400;
    scrollElement.dispatchEvent(new Event('scroll'));
    queuedFrame?.(0);
    expect(scrollElement.scrollTop).toBe(400);
    cleanup();
  });
});

function createScrollElement(cacheKey: string, scrollHeight = 5_000): HTMLDivElement {
  const scrollElement = document.createElement('div');
  Object.defineProperty(scrollElement, 'clientHeight', { configurable: true, value: 800 });
  Object.defineProperty(scrollElement, 'scrollHeight', { configurable: true, value: scrollHeight });
  scrollElement.scrollTo = vi.fn((first?: number | ScrollToOptions, second?: number) => {
    const top = typeof first === 'object' ? (first.top ?? 0) : (second ?? 0);
    scrollElement.scrollTop = Math.min(top, scrollElement.scrollHeight - scrollElement.clientHeight);
  }) as HTMLDivElement['scrollTo'];
  const list = document.createElement('div');
  list.dataset.testid = 'suggestions-list';
  list.dataset.suggestionsCacheKey = cacheKey;
  scrollElement.appendChild(list);
  return scrollElement;
}
