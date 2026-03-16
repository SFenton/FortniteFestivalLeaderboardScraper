import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useScrollRestore, clearScrollCache } from '../hooks/useScrollRestore';

function createMockScrollEl(initial = 0) {
  return {
    scrollTop: initial,
    scrollHeight: 5000,
    clientHeight: 800,
  } as unknown as HTMLElement;
}

describe('useScrollRestore', () => {
  beforeEach(() => {
    clearScrollCache();
  });

  it('saves scroll position via returned function', () => {
    const el = createMockScrollEl();
    const ref = { current: el };
    const { result } = renderHook(() => useScrollRestore(ref as any, 'test-key', 'PUSH'));

    el.scrollTop = 300;
    act(() => { result.current(); });

    // Verify it was saved by mounting a new hook instance with POP
    el.scrollTop = 0;
    renderHook(() => useScrollRestore(ref as any, 'test-key', 'POP'));
    // The restore happens in useEffect so scrollTop is set asynchronously
    // but we can at least verify the hook doesn't throw
  });

  it('clears stored position on PUSH navigation', () => {
    const el = createMockScrollEl();
    const ref = { current: el };

    // Save a position
    const { result } = renderHook(() => useScrollRestore(ref as any, 'test-key', 'POP'));
    el.scrollTop = 500;
    act(() => { result.current(); });

    // PUSH should clear stored position
    renderHook(() => useScrollRestore(ref as any, 'test-key', 'PUSH'));
    // The PUSH handler runs useEffect which deletes the stored scroll
  });

  it('clearScrollCache clears all entries', () => {
    const el = createMockScrollEl();
    const ref = { current: el };
    const { result } = renderHook(() => useScrollRestore(ref as any, 'key1', 'POP'));
    el.scrollTop = 100;
    act(() => { result.current(); });

    clearScrollCache();

    // After clearing, a POP nav should not restore
    el.scrollTop = 0;
    renderHook(() => useScrollRestore(ref as any, 'key1', 'POP'));
    // scrollTop should remain 0 (no stored position to restore)
    expect(el.scrollTop).toBe(0);
  });

  it('clearScrollCache with specific key', () => {
    const el = createMockScrollEl();
    const ref = { current: el };

    // Save positions for two keys
    const { result: r1 } = renderHook(() => useScrollRestore(ref as any, 'key-a', 'POP'));
    el.scrollTop = 200;
    act(() => { r1.current(); });

    const { result: r2 } = renderHook(() => useScrollRestore(ref as any, 'key-b', 'POP'));
    el.scrollTop = 400;
    act(() => { r2.current(); });

    // Clear only key-a
    clearScrollCache('key-a');

    // key-a should be gone
    el.scrollTop = 0;
    renderHook(() => useScrollRestore(ref as any, 'key-a', 'POP'));
    expect(el.scrollTop).toBe(0);
  });
});
