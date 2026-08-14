import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  render,
  renderHook,
  act,
  fireEvent,
  screen,
} from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { StrictMode, Suspense, startTransition, useState } from 'react';
import {
  usePageTransition,
  clearPageTransitionCache,
  hasVisitedPage,
} from '../../../src/hooks/ui/usePageTransition';
import { LoadPhase } from '@festival/core/runtime';

function wrapper({ children }: { children: React.ReactNode }) {
  return <MemoryRouter>{children}</MemoryRouter>;
}

function strictWrapper({ children }: { children: React.ReactNode }) {
  return (
    <StrictMode>
      <MemoryRouter>{children}</MemoryRouter>
    </StrictMode>
  );
}

describe('usePageTransition', () => {
  beforeEach(() => {
    clearPageTransitionCache();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('returns Loading phase when data is not ready', () => {
    const { result } = renderHook(
      () => usePageTransition('test-key', false),
      { wrapper },
    );
    expect(result.current.phase).toBe(LoadPhase.Loading);
    expect(result.current.shouldStagger).toBe(true);
  });

  it('transitions to ContentIn when data becomes ready', async () => {
    const { result, rerender } = renderHook(
      ({ ready }) => usePageTransition('test-key', ready),
      { wrapper, initialProps: { ready: false } },
    );
    expect(result.current.phase).toBe(LoadPhase.Loading);

    rerender({ ready: true });
    expect(result.current.phase).toBe(LoadPhase.SpinnerOut);

    // Advance past spinner fade (500ms + buffer)
    await act(async () => { vi.advanceTimersByTime(600); });
    expect(result.current.phase).toBe(LoadPhase.ContentIn);
  });

  it('skips animation on return visit (key already visited + POP)', async () => {
    // First visit
    const { unmount } = renderHook(
      () => usePageTransition('return-key', true),
      { wrapper },
    );
    await act(async () => { vi.advanceTimersByTime(600); });
    unmount();

    // Second visit with cached data
    const { result } = renderHook(
      () => usePageTransition('return-key', true, true),
      { wrapper },
    );
    expect(result.current.phase).toBe(LoadPhase.ContentIn);
  });

  it('clearPageTransitionCache clears all visited keys', async () => {
    renderHook(() => usePageTransition('key1', true), { wrapper });
    await act(async () => { vi.advanceTimersByTime(600); });
    clearPageTransitionCache();

    const { result } = renderHook(
      () => usePageTransition('key1', true, true),
      { wrapper },
    );
    // After clearing, should stagger again
    expect(result.current.shouldStagger).toBe(true);
  });

  it('clearPageTransitionCache with specific key', async () => {
    renderHook(() => usePageTransition('key-a', true), { wrapper });
    renderHook(() => usePageTransition('key-b', true), { wrapper });
    await act(async () => { vi.advanceTimersByTime(600); });

    clearPageTransitionCache('key-a');

    const { result: rA } = renderHook(
      () => usePageTransition('key-a', true, true),
      { wrapper },
    );
    expect(rA.current.shouldStagger).toBe(true);
  });

  it('does not mark an unready or abandoned render as visited', () => {
    const first = renderHook(
      () => usePageTransition('abandoned-key', false),
      { wrapper },
    );
    expect(hasVisitedPage('abandoned-key')).toBe(false);
    first.unmount();

    const second = renderHook(
      () => usePageTransition('abandoned-key', true, true),
      { wrapper },
    );
    expect(second.result.current.shouldStagger).toBe(true);
  });

  it('keeps the first committed StrictMode visit staggered', () => {
    const first = renderHook(
      () => usePageTransition('strict-key', true, true),
      { wrapper: strictWrapper },
    );
    expect(first.result.current.shouldStagger).toBe(true);
    expect(hasVisitedPage('strict-key')).toBe(true);
    first.unmount();

    const second = renderHook(
      () => usePageTransition('strict-key', true, true),
      { wrapper: strictWrapper },
    );
    expect(second.result.current.shouldStagger).toBe(false);
  });

  it('resets load and visit state when the cache key changes', async () => {
    const { result, rerender } = renderHook(
      ({ cacheKey, ready }) => usePageTransition(cacheKey, ready, true),
      {
        wrapper,
        initialProps: { cacheKey: 'key-a', ready: true },
      },
    );
    expect(result.current.phase).toBe(LoadPhase.ContentIn);
    expect(hasVisitedPage('key-a')).toBe(true);

    rerender({ cacheKey: 'key-b', ready: false });
    expect(result.current.phase).toBe(LoadPhase.Loading);
    expect(result.current.shouldStagger).toBe(true);
    expect(hasVisitedPage('key-b')).toBe(false);

    rerender({ cacheKey: 'key-b', ready: true });
    expect(result.current.phase).toBe(LoadPhase.SpinnerOut);
    await act(async () => { vi.advanceTimersByTime(600); });
    expect(result.current.phase).toBe(LoadPhase.ContentIn);
    expect(hasVisitedPage('key-b')).toBe(true);
  });

  it('retries a suspended key change from state owned by the new key', async () => {
    let release!: () => void;
    const suspended = new Promise<void>(resolve => {
      release = resolve;
    });
    let shouldSuspend = true;

    function Probe({ cacheKey, ready }: { cacheKey: string; ready: boolean }) {
      const transition = usePageTransition(cacheKey, ready, true);
      if (cacheKey === 'key-b' && shouldSuspend) throw suspended;
      return <div data-testid="phase">{transition.phase}</div>;
    }

    function Harness() {
      const [page, setPage] = useState({ cacheKey: 'key-a', ready: true });
      return (
        <MemoryRouter>
          <button
            type="button"
            onClick={() => {
              startTransition(() => {
                setPage({ cacheKey: 'key-b', ready: false });
              });
            }}
          >
            Change key
          </button>
          <Suspense fallback={<div>Loading route</div>}>
            <Probe {...page} />
          </Suspense>
        </MemoryRouter>
      );
    }

    render(<Harness />);
    expect(screen.getByTestId('phase')).toHaveTextContent(LoadPhase.ContentIn);
    fireEvent.click(screen.getByRole('button', { name: 'Change key' }));
    expect(screen.getByTestId('phase')).toHaveTextContent(LoadPhase.ContentIn);

    shouldSuspend = false;
    await act(async () => {
      release();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByTestId('phase')).toHaveTextContent(LoadPhase.Loading);
    expect(hasVisitedPage('key-b')).toBe(false);
  });
});
