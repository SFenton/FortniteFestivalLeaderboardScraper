import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { staggerCompletionDelay, useStagger } from '../../../src/hooks/ui/useStagger';
import { FADE_DURATION, STAGGER_INTERVAL } from '@festival/theme';

describe('useStagger', () => {
  it('returns undefined styles when shouldStagger is false', () => {
    const { result } = renderHook(() => useStagger(false));
    expect(result.current.forDelay(100)).toBeUndefined();
    expect(result.current.forIndex(0)).toBeUndefined();
    expect(result.current.next()).toBeUndefined();
  });

  it('returns animation style for forDelay', () => {
    const { result } = renderHook(() => useStagger(true));
    const style = result.current.forDelay(200);
    expect(style).toBeDefined();
    expect(style!.opacity).toBe(0);
    expect(style!.animation).toContain('fadeInUp');
    expect(style!.animation).toContain('200ms');
  });

  it('returns animation style for forIndex', () => {
    const { result } = renderHook(() => useStagger(true));
    const style = result.current.forIndex(2);
    expect(style).toBeDefined();
    expect(style!.animation).toContain(`${2 * STAGGER_INTERVAL}ms`);
  });

  it('forIndex supports offset', () => {
    const { result } = renderHook(() => useStagger(true));
    const style = result.current.forIndex(1, 100);
    expect(style!.animation).toContain(`${100 + STAGGER_INTERVAL}ms`);
  });

  it('next() auto-increments', () => {
    const { result } = renderHook(() => useStagger(true));
    const s0 = result.current.next();
    const s1 = result.current.next();
    const s2 = result.current.next();
    expect(s0!.animation).toContain('0ms forwards');
    expect(s1!.animation).toContain(`${STAGGER_INTERVAL}ms`);
    expect(s2!.animation).toContain(`${2 * STAGGER_INTERVAL}ms`);
  });

  it('respects custom interval', () => {
    const { result } = renderHook(() => useStagger(true, 50));
    result.current.next(); // idx 0
    const s1 = result.current.next(); // idx 1
    expect(s1!.animation).toContain('50ms');
  });

  it('clearAnim removes opacity and animation from element', () => {
    const { result } = renderHook(() => useStagger(true));
    const el = { style: { opacity: '0', animation: 'fadeInUp 400ms' } };
    const event = { currentTarget: el } as unknown as React.AnimationEvent<HTMLElement>;
    result.current.clearAnim(event);
    expect(el.style.opacity).toBe('');
    expect(el.style.animation).toBe('');
  });

  it('computes the total completion delay for a staggered sequence', () => {
    expect(staggerCompletionDelay(0)).toBe(0);
    expect(staggerCompletionDelay(1)).toBe(FADE_DURATION);
    expect(staggerCompletionDelay(3)).toBe((2 * STAGGER_INTERVAL) + FADE_DURATION);
  });
});
