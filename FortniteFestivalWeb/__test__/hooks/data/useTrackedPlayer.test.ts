import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useTrackedPlayer } from '../../../src/hooks/data/useTrackedPlayer';

const STORAGE_KEY = 'fst:trackedPlayer';

beforeEach(() => {
  localStorage.clear();
});

describe('useTrackedPlayer', () => {
  it('returns null when no player is stored', () => {
    const { result } = renderHook(() => useTrackedPlayer());
    expect(result.current.player).toBeNull();
  });

  it('loads player from localStorage', () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ accountId: 'abc', displayName: 'TestUser' }));
    const { result } = renderHook(() => useTrackedPlayer());
    expect(result.current.player).toEqual({ accountId: 'abc', displayName: 'TestUser' });
  });

  it('sets player and persists to localStorage', () => {
    const { result } = renderHook(() => useTrackedPlayer());
    act(() => {
      result.current.setPlayer({ accountId: '123', displayName: 'Player1' });
    });
    expect(result.current.player).toEqual({ accountId: '123', displayName: 'Player1' });
    expect(JSON.parse(localStorage.getItem(STORAGE_KEY)!).accountId).toBe('123');
  });

  it('clears player and removes from localStorage', () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ accountId: 'abc', displayName: 'TestUser' }));
    const { result } = renderHook(() => useTrackedPlayer());
    act(() => {
      result.current.clearPlayer();
    });
    expect(result.current.player).toBeNull();
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('defaults displayName to "Unknown User" when empty', () => {
    const { result } = renderHook(() => useTrackedPlayer());
    act(() => {
      result.current.setPlayer({ accountId: '123', displayName: '' });
    });
    expect(result.current.player?.displayName).toBe('Unknown User');
  });

  it('handles invalid localStorage gracefully', () => {
    localStorage.setItem(STORAGE_KEY, 'garbage');
    const { result } = renderHook(() => useTrackedPlayer());
    expect(result.current.player).toBeNull();
  });

  it('defaults displayName to Unknown User when loaded with empty name', () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ accountId: 'abc', displayName: '' }));
    const { result } = renderHook(() => useTrackedPlayer());
    expect(result.current.player).toEqual({ accountId: 'abc', displayName: 'Unknown User' });
  });

  it('returns null when stored object has no accountId', () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ foo: 'bar' }));
    const { result } = renderHook(() => useTrackedPlayer());
    expect(result.current.player).toBeNull();
  });

  it('returns null when stored value is JSON null', () => {
    localStorage.setItem(STORAGE_KEY, 'null');
    const { result } = renderHook(() => useTrackedPlayer());
    expect(result.current.player).toBeNull();
  });
});
