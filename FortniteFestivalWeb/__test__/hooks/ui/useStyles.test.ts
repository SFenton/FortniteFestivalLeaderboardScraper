import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useStyles } from '../../../src/hooks/ui/useStyles';

describe('useStyles', () => {
  it('returns the style object from factory', () => {
    const { result } = renderHook(() =>
      useStyles(() => ({
        card: { backgroundColor: 'red', padding: 10 },
        title: { fontSize: 22, fontWeight: 700 },
      })),
    );
    expect(result.current.card).toEqual({ backgroundColor: 'red', padding: 10 });
    expect(result.current.title).toEqual({ fontSize: 22, fontWeight: 700 });
  });

  it('memoizes across re-renders', () => {
    const { result, rerender } = renderHook(() =>
      useStyles(() => ({ box: { margin: 5 } })),
    );
    const first = result.current;
    rerender();
    expect(result.current).toBe(first); // same reference
  });

  it('recomputes when deps change', () => {
    const { result, rerender } = renderHook(
      ({ size }) => useStyles(() => ({ box: { width: size } }), [size]),
      { initialProps: { size: 100 } },
    );
    const first = result.current;
    rerender({ size: 200 });
    expect(result.current).not.toBe(first);
    expect(result.current.box.width).toBe(200);
  });
});
