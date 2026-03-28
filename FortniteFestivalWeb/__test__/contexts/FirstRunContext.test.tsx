import { describe, it, expect, beforeEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { ReactNode } from 'react';

const mockFlags = vi.hoisted(() => ({
  shop: true, rivals: true, compete: true, leaderboards: true, firstRun: true,
}));

vi.mock('../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => mockFlags,
}));

import { FirstRunProvider, useFirstRunContext } from '../../src/contexts/FirstRunContext';
import { contentHash, loadSeenSlides } from '../../src/firstRun/types';
import type { FirstRunSlideDef } from '../../src/firstRun/types';

function wrapper({ children }: { children: ReactNode }) {
  return <FirstRunProvider>{children}</FirstRunProvider>;
}

function makeSlide(id: string, overrides: Partial<FirstRunSlideDef> = {}): FirstRunSlideDef {
  return {
    id,
    version: 1,
    title: `Title ${id}`,
    description: `Desc ${id}`,
    render: () => null,
    ...overrides,
  };
}

describe('FirstRunContext', () => {
  beforeEach(() => {
    localStorage.clear();
    mockFlags.firstRun = true;
  });

  it('throws when used outside provider', () => {
    expect(() => {
      renderHook(() => useFirstRunContext());
    }).toThrow('useFirstRunContext must be used within a FirstRunProvider');
  });

  it('register and getAllSlides returns registered slides', () => {
    const slides = [makeSlide('a'), makeSlide('b')];
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });

    act(() => {
      result.current.register('page1', 'Page One', slides);
    });

    expect(result.current.getAllSlides('page1')).toEqual(slides);
    expect(result.current.registeredPages).toEqual([{ pageKey: 'page1', label: 'Page One' }]);
  });

  it('unregister removes slides', () => {
    const slides = [makeSlide('a')];
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });

    act(() => {
      result.current.register('page1', 'P1', slides);
    });
    act(() => {
      result.current.unregister('page1');
    });

    expect(result.current.getAllSlides('page1')).toEqual([]);
    expect(result.current.registeredPages).toEqual([]);
  });

  it('getUnseenSlides returns only unseen slides that pass gate', () => {
    const gated = makeSlide('gated', { gate: ctx => ctx.hasPlayer });
    const ungated = makeSlide('ungated');
    const seen = makeSlide('seen');

    const { result } = renderHook(() => useFirstRunContext(), { wrapper });

    // Pre-seed "seen" as already viewed
    act(() => {
      result.current.register('p', 'P', [gated, ungated, seen]);
      result.current.markSeen([seen]);
    });

    // Gate fails: hasPlayer = false
    const unseenNoPlayer = result.current.getUnseenSlides('p', { hasPlayer: false });
    expect(unseenNoPlayer.map(s => s.id)).toEqual(['ungated']);

    // Gate passes: hasPlayer = true
    const unseenWithPlayer = result.current.getUnseenSlides('p', { hasPlayer: true });
    expect(unseenWithPlayer.map(s => s.id)).toEqual(['gated', 'ungated']);
  });

  it('getUnseenSlides returns empty for unknown page', () => {
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });
    expect(result.current.getUnseenSlides('nope', { hasPlayer: false })).toEqual([]);
  });

  it('markSeen persists to localStorage', () => {
    const slide = makeSlide('x');
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });

    act(() => {
      result.current.register('p', 'P', [slide]);
      result.current.markSeen([slide]);
    });

    const stored = loadSeenSlides();
    expect(stored['x']).toBeDefined();
    expect(stored['x']!.version).toBe(1);
    expect(stored['x']!.hash).toBe(contentHash(slide.title + slide.description));
  });

  it('markSeen uses contentKey for hash when provided', () => {
    const slide = makeSlide('y', { contentKey: 'my-content-key' });
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });

    act(() => {
      result.current.register('p', 'P', [slide]);
      result.current.markSeen([slide]);
    });

    const stored = loadSeenSlides();
    expect(stored['y']).toBeDefined();
    expect(stored['y']!.hash).toBe(contentHash('my-content-key'));
  });

  it('resetPage clears seen state for that page only', () => {
    const slideA = makeSlide('a');
    const slideB = makeSlide('b');
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });

    act(() => {
      result.current.register('p1', 'P1', [slideA]);
      result.current.register('p2', 'P2', [slideB]);
      result.current.markSeen([slideA, slideB]);
    });

    act(() => {
      result.current.resetPage('p1');
    });

    const stored = loadSeenSlides();
    expect(stored['a']).toBeUndefined();
    expect(stored['b']).toBeDefined();
  });

  it('resetPage is no-op for unknown page', () => {
    const slide = makeSlide('z');
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });

    act(() => {
      result.current.register('p', 'P', [slide]);
      result.current.markSeen([slide]);
    });

    act(() => {
      result.current.resetPage('unknown');
    });

    expect(loadSeenSlides()['z']).toBeDefined();
  });

  it('resetAll clears all seen state', () => {
    const slides = [makeSlide('a'), makeSlide('b')];
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });

    act(() => {
      result.current.register('p', 'P', slides);
      result.current.markSeen(slides);
    });

    act(() => {
      result.current.resetAll();
    });

    expect(loadSeenSlides()).toEqual({});
  });

  it('setActiveCarousel updates activeCarouselKey', () => {
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });
    expect(result.current.activeCarouselKey).toBeNull();

    act(() => {
      result.current.setActiveCarousel('songs');
    });
    expect(result.current.activeCarouselKey).toBe('songs');

    act(() => {
      result.current.setActiveCarousel(null);
    });
    expect(result.current.activeCarouselKey).toBeNull();
  });

  it('enabled reflects the firstRun feature flag', () => {
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });
    expect(result.current.enabled).toBe(true);
  });

  it('enabled is false when firstRun flag is off', () => {
    mockFlags.firstRun = false;
    const { result } = renderHook(() => useFirstRunContext(), { wrapper });
    expect(result.current.enabled).toBe(false);
  });
});
