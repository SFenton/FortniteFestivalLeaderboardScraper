import { describe, it, expect, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useModalDraft } from '../../../src/hooks/ui/useModalDraft';

describe('useModalDraft', () => {
  it('detects no changes when draft matches saved', () => {
    const onCancel = vi.fn();
    const { result } = renderHook(() =>
      useModalDraft({ a: 1 }, { a: 1 }, onCancel),
    );
    expect(result.current.hasChanges).toBe(false);
  });

  it('detects changes when draft differs from saved', () => {
    const onCancel = vi.fn();
    const { result } = renderHook(() =>
      useModalDraft({ a: 2 }, { a: 1 }, onCancel),
    );
    expect(result.current.hasChanges).toBe(true);
  });

  it('returns hasChanges=true when savedDraft is undefined', () => {
    const onCancel = vi.fn();
    const { result } = renderHook(() =>
      useModalDraft({ a: 1 }, undefined, onCancel),
    );
    expect(result.current.hasChanges).toBe(true);
  });

  it('handleClose calls onCancel when no changes', () => {
    const onCancel = vi.fn();
    const { result } = renderHook(() =>
      useModalDraft({ a: 1 }, { a: 1 }, onCancel),
    );
    act(() => { result.current.handleClose(); });
    expect(onCancel).toHaveBeenCalled();
  });

  it('handleClose opens confirm dialog when changes exist', () => {
    const onCancel = vi.fn();
    const { result } = renderHook(() =>
      useModalDraft({ a: 2 }, { a: 1 }, onCancel),
    );
    act(() => { result.current.handleClose(); });
    expect(result.current.confirmOpen).toBe(true);
    expect(onCancel).not.toHaveBeenCalled();
  });

  it('confirmDiscard closes confirm dialog and calls onCancel', () => {
    const onCancel = vi.fn();
    const { result } = renderHook(() =>
      useModalDraft({ a: 2 }, { a: 1 }, onCancel),
    );
    act(() => { result.current.handleClose(); });
    expect(result.current.confirmOpen).toBe(true);
    act(() => { result.current.confirmDiscard(); });
    expect(result.current.confirmOpen).toBe(false);
    expect(onCancel).toHaveBeenCalled();
  });

  it('supports custom isEqual function', () => {
    const onCancel = vi.fn();
    const isEqual = (a: { x: number }, b: { x: number }) => a.x === b.x;
    const { result } = renderHook(() =>
      useModalDraft({ x: 1, extra: 'a' } as any, { x: 1, extra: 'b' } as any, onCancel, isEqual),
    );
    // Custom isEqual only checks .x, so no changes detected
    expect(result.current.hasChanges).toBe(false);
  });
});
