import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useModalState } from '../hooks/useModalState';

describe('useModalState', () => {
  const defaults = () => ({ sortMode: 'title', ascending: true });

  it('starts closed with default draft', () => {
    const { result } = renderHook(() => useModalState(defaults));
    expect(result.current.visible).toBe(false);
    expect(result.current.draft).toEqual({ sortMode: 'title', ascending: true });
  });

  it('opens with provided current values', () => {
    const { result } = renderHook(() => useModalState(defaults));

    act(() => {
      result.current.open({ sortMode: 'score', ascending: false });
    });

    expect(result.current.visible).toBe(true);
    expect(result.current.draft).toEqual({ sortMode: 'score', ascending: false });
  });

  it('closes without changing draft', () => {
    const { result } = renderHook(() => useModalState(defaults));

    act(() => {
      result.current.open({ sortMode: 'score', ascending: false });
    });
    act(() => {
      result.current.close();
    });

    expect(result.current.visible).toBe(false);
    expect(result.current.draft).toEqual({ sortMode: 'score', ascending: false });
  });

  it('resets draft to defaults', () => {
    const { result } = renderHook(() => useModalState(defaults));

    act(() => {
      result.current.open({ sortMode: 'score', ascending: false });
    });
    act(() => {
      result.current.reset();
    });

    expect(result.current.draft).toEqual({ sortMode: 'title', ascending: true });
    // Modal stays open after reset
    expect(result.current.visible).toBe(true);
  });

  it('allows draft mutation via setDraft', () => {
    const { result } = renderHook(() => useModalState(defaults));

    act(() => {
      result.current.open({ sortMode: 'title', ascending: true });
    });
    act(() => {
      result.current.setDraft({ sortMode: 'artist', ascending: false });
    });

    expect(result.current.draft).toEqual({ sortMode: 'artist', ascending: false });
  });
});
