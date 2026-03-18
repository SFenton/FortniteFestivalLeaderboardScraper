import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../hooks/ui/useVisualViewport';

describe('useVisualViewportHeight', () => {
  it('returns a number (window.innerHeight or visualViewport.height)', () => {
    const { result } = renderHook(() => useVisualViewportHeight());
    expect(typeof result.current).toBe('number');
    expect(result.current).toBeGreaterThan(0);
  });
});

describe('useVisualViewportOffsetTop', () => {
  it('returns 0 by default (no virtual keyboard)', () => {
    const { result } = renderHook(() => useVisualViewportOffsetTop());
    expect(result.current).toBe(0);
  });
});
