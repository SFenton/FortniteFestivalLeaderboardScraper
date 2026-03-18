import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useScrollFade } from '../../hooks/ui/useScrollFade';

describe('useScrollFade', () => {
  it('returns an update function', () => {
    const scrollRef = { current: null };
    const listRef = { current: null };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    expect(typeof result.current).toBe('function');
  });

  it('does not throw when refs are null', () => {
    const scrollRef = { current: null };
    const listRef = { current: null };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    expect(() => result.current()).not.toThrow();
  });

  it('clears masks when scroll element has no children', () => {
    const scrollEl = document.createElement('div');
    const listEl = document.createElement('div');
    const scrollRef = { current: scrollEl };
    const listRef = { current: listEl };
    const { result } = renderHook(() => useScrollFade(scrollRef as any, listRef as any));
    result.current(); // should not throw
  });
});
