import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useStaggerStyle, staggerMs, buildStaggerStyle, clearStaggerStyle } from '../../../hooks/ui/useStaggerStyle';

describe('staggerMs', () => {
  it('computes delay for index 0 with no base', () => {
    expect(staggerMs(0, 60, 80)).toBe(80);
  });

  it('computes delay for index with base', () => {
    expect(staggerMs(2, 60, 80, 100)).toBe(100 + 80 + 2 * 60);
  });

  it('computes delay with zero offset', () => {
    expect(staggerMs(3, 50)).toBe(150);
  });

  it('computes delay for index 0 with base only', () => {
    expect(staggerMs(0, 60, 0, 200)).toBe(200);
  });
});

describe('useStaggerStyle', () => {
  it('returns animation style for a given delay', () => {
    const { result } = renderHook(() => useStaggerStyle(200));
    expect(result.current.style).toBeDefined();
    expect(result.current.style!.opacity).toBe(0);
    expect(result.current.style!.animation).toContain('200ms');
  });

  it('returns undefined style when skip is true', () => {
    const { result } = renderHook(() => useStaggerStyle(200, { skip: true }));
    expect(result.current.style).toBeUndefined();
  });

  it('returns undefined style when delay is null', () => {
    const { result } = renderHook(() => useStaggerStyle(null));
    expect(result.current.style).toBeUndefined();
  });

  it('returns undefined style when delay is undefined', () => {
    const { result } = renderHook(() => useStaggerStyle(undefined));
    expect(result.current.style).toBeUndefined();
  });

  it('uses custom duration', () => {
    const { result } = renderHook(() => useStaggerStyle(100, { duration: 300 }));
    expect(result.current.style!.animation).toContain('300ms');
  });

  it('uses custom animation name', () => {
    const { result } = renderHook(() => useStaggerStyle(100, { animation: 'slideUp' }));
    expect(result.current.style!.animation).toContain('slideUp');
  });

  it('onAnimationEnd clears inline styles', () => {
    const { result } = renderHook(() => useStaggerStyle(100));
    const el = document.createElement('div');
    el.style.opacity = '0';
    el.style.animation = 'test';
    result.current.onAnimationEnd({ currentTarget: el } as any);
    expect(el.style.opacity).toBe('');
    expect(el.style.animation).toBe('');
  });

  it('returns stable onAnimationEnd reference', () => {
    const { result, rerender } = renderHook(() => useStaggerStyle(100));
    const first = result.current.onAnimationEnd;
    rerender();
    expect(result.current.onAnimationEnd).toBe(first);
  });
});

describe('buildStaggerStyle', () => {
  it('returns animation style for a delay', () => {
    const style = buildStaggerStyle(150);
    expect(style).toBeDefined();
    expect(style!.opacity).toBe(0);
    expect(style!.animation).toContain('150ms');
    expect(style!.animation).toContain('fadeInUp');
  });

  it('returns undefined for null delay', () => {
    expect(buildStaggerStyle(null)).toBeUndefined();
  });

  it('returns undefined for undefined delay', () => {
    expect(buildStaggerStyle(undefined)).toBeUndefined();
  });

  it('uses custom duration and animation', () => {
    const style = buildStaggerStyle(100, { duration: 300, animation: 'slideIn' });
    expect(style!.animation).toContain('300ms');
    expect(style!.animation).toContain('slideIn');
  });
});

describe('clearStaggerStyle', () => {
  it('clears opacity and animation from element', () => {
    const el = document.createElement('div');
    el.style.opacity = '0';
    el.style.animation = 'fadeInUp 400ms ease-out 100ms forwards';
    clearStaggerStyle({ currentTarget: el } as any);
    expect(el.style.opacity).toBe('');
    expect(el.style.animation).toBe('');
  });
});
