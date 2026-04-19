import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { ReactNode } from 'react';

import { FirstRunProvider, useFirstRunContext } from '../../../src/contexts/FirstRunContext';
import { useFirstRun, useFirstRunReplay } from '../../../src/hooks/ui/useFirstRun';
import { contentHash } from '../../../src/firstRun/types';
import type { FirstRunSlideDef, FirstRunGateContext } from '../../../src/firstRun/types';

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

function wrapper({ children }: { children: ReactNode }) {
  return <FirstRunProvider>{children}</FirstRunProvider>;
}

// Helper: register slides then call useFirstRun
function useRegisteredFirstRun(pageKey: string, slides: FirstRunSlideDef[], gateCtx: FirstRunGateContext) {
  const ctx = useFirstRunContext();
  // Register on first render
  const registered = React.useRef(false);
  if (!registered.current) {
    ctx.register(pageKey, pageKey, slides);
    registered.current = true;
  }
  return useFirstRun(pageKey, gateCtx);
}

import React from 'react';

describe('useFirstRun', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('returns unseen slides for a registered page', () => {
    const slides = [makeSlide('a'), makeSlide('b')];
    const { result } = renderHook(
      () => useRegisteredFirstRun('page1', slides, { hasPlayer: false }),
      { wrapper },
    );
    expect(result.current.slides).toHaveLength(2);
    expect(result.current.show).toBe(true);
  });

  it('returns no slides when gate context ready=false', () => {
    const slides = [makeSlide('a')];
    const { result } = renderHook(
      () => useRegisteredFirstRun('page1', slides, { hasPlayer: false, ready: false }),
      { wrapper },
    );
    expect(result.current.slides).toHaveLength(0);
    expect(result.current.show).toBe(false);
  });

  it('alwaysShow bypasses seen state and filters by gate', () => {
    // Pre-seed all slides as seen
    const slide = makeSlide('a', { gate: ctx => ctx.hasPlayer });
    const seenData = {
      a: { version: 1, hash: contentHash(slide.title + slide.description), seenAt: new Date().toISOString() },
    };
    localStorage.setItem('fst:firstRun', JSON.stringify(seenData));

    const { result } = renderHook(
      () => useRegisteredFirstRun('page1', [slide], { hasPlayer: true, alwaysShow: true }),
      { wrapper },
    );
    expect(result.current.slides).toHaveLength(1);
  });

  it('alwaysShow excludes slides that fail gate', () => {
    const slide = makeSlide('a', { gate: ctx => ctx.hasPlayer });
    const { result } = renderHook(
      () => useRegisteredFirstRun('page1', [slide], { hasPlayer: false, alwaysShow: true }),
      { wrapper },
    );
    expect(result.current.slides).toHaveLength(0);
  });

  it('dismiss marks slides as seen and begins closing', () => {
    const slides = [makeSlide('a')];
    const { result } = renderHook(
      () => useRegisteredFirstRun('page1', slides, { hasPlayer: false }),
      { wrapper },
    );

    act(() => {
      result.current.dismiss();
    });
    // Still showing during exit animation (closing=true freezes slides)
    expect(result.current.show).toBe(true);
  });

  it('onExitComplete clears slides after dismiss', () => {
    const slides = [makeSlide('a')];
    const { result } = renderHook(
      () => useRegisteredFirstRun('page1', slides, { hasPlayer: false }),
      { wrapper },
    );

    act(() => {
      result.current.dismiss();
    });
    act(() => {
      result.current.onExitComplete();
    });
    expect(result.current.show).toBe(false);
    expect(result.current.slides).toHaveLength(0);
  });

  it('returns empty when all slides already seen', () => {
    const slide = makeSlide('a');
    const seenData = {
      a: { version: 1, hash: contentHash(slide.title + slide.description), seenAt: new Date().toISOString() },
    };
    localStorage.setItem('fst:firstRun', JSON.stringify(seenData));

    const { result } = renderHook(
      () => useRegisteredFirstRun('page1', [slide], { hasPlayer: false }),
      { wrapper },
    );
    expect(result.current.slides).toHaveLength(0);
    expect(result.current.show).toBe(false);
  });

  it('dismiss is safe when no computed slides (all already seen)', () => {
    const slide = makeSlide('a');
    const seenData = {
      a: { version: 1, hash: contentHash(slide.title + slide.description), seenAt: new Date().toISOString() },
    };
    localStorage.setItem('fst:firstRun', JSON.stringify(seenData));

    const { result } = renderHook(
      () => useRegisteredFirstRun('page1', [slide], { hasPlayer: false }),
      { wrapper },
    );
    // Dismiss when no slides — should not throw
    act(() => {
      result.current.dismiss();
    });
    expect(result.current.show).toBe(false);
  });

});

describe('useFirstRunReplay', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  function useRegisteredReplay(pageKey: string, slides: FirstRunSlideDef[]) {
    const ctx = useFirstRunContext();
    const registered = React.useRef(false);
    if (!registered.current) {
      ctx.register(pageKey, pageKey, slides);
      registered.current = true;
    }
    return useFirstRunReplay(pageKey);
  }

  it('returns all slides regardless of seen state', () => {
    const slide = makeSlide('a');
    const seenData = {
      a: { version: 1, hash: contentHash(slide.title + slide.description), seenAt: new Date().toISOString() },
    };
    localStorage.setItem('fst:firstRun', JSON.stringify(seenData));

    const { result } = renderHook(
      () => useRegisteredReplay('page1', [slide]),
      { wrapper },
    );
    expect(result.current.slides).toHaveLength(1);
    expect(result.current.show).toBe(false);
  });

  it('open resets page and shows carousel', () => {
    const slides = [makeSlide('a')];
    const { result } = renderHook(
      () => useRegisteredReplay('page1', slides),
      { wrapper },
    );

    act(() => {
      result.current.open();
    });
    expect(result.current.show).toBe(true);
  });

  it('dismiss marks slides as seen and closes after onExitComplete', () => {
    const slides = [makeSlide('a')];
    const { result } = renderHook(
      () => useRegisteredReplay('page1', slides),
      { wrapper },
    );

    act(() => {
      result.current.open();
    });
    act(() => {
      result.current.dismiss();
    });
    expect(result.current.show).toBe(true);
    act(() => {
      result.current.onExitComplete();
    });
    expect(result.current.show).toBe(false);
  });

  it('dismiss with no slides is a no-op for markSeen', () => {
    const { result } = renderHook(
      () => useRegisteredReplay('empty-page', []),
      { wrapper },
    );

    act(() => {
      result.current.open();
    });
    act(() => {
      result.current.dismiss();
    });
    expect(result.current.show).toBe(true);
    act(() => {
      result.current.onExitComplete();
    });
    expect(result.current.show).toBe(false);
  });

});
