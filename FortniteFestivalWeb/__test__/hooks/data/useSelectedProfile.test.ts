import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useSelectedProfile } from '../../../src/hooks/data/useSelectedProfile';

const SELECTED_PROFILE_STORAGE_KEY = 'fst:selectedProfile';
const LEGACY_PLAYER_STORAGE_KEY = 'fst:trackedPlayer';

beforeEach(() => {
  localStorage.clear();
});

describe('useSelectedProfile', () => {
  it('returns null when no profile is stored', () => {
    const { result } = renderHook(() => useSelectedProfile());
    expect(result.current.profile).toBeNull();
  });

  it('migrates a legacy tracked player into selected profile state', () => {
    localStorage.setItem(LEGACY_PLAYER_STORAGE_KEY, JSON.stringify({ accountId: 'p1', displayName: 'Player One' }));

    const { result } = renderHook(() => useSelectedProfile());

    expect(result.current.profile).toEqual({ type: 'player', accountId: 'p1', displayName: 'Player One' });
    expect(JSON.parse(localStorage.getItem(SELECTED_PROFILE_STORAGE_KEY)!).type).toBe('player');
  });

  it('selects a player and mirrors the legacy player key', () => {
    const { result } = renderHook(() => useSelectedProfile());

    act(() => {
      result.current.selectPlayer({ accountId: 'p1', displayName: '' });
    });

    expect(result.current.profile).toEqual({ type: 'player', accountId: 'p1', displayName: 'Unknown User' });
    expect(JSON.parse(localStorage.getItem(LEGACY_PLAYER_STORAGE_KEY)!).accountId).toBe('p1');
  });

  it('selects a band and removes the legacy player key', () => {
    localStorage.setItem(LEGACY_PLAYER_STORAGE_KEY, JSON.stringify({ accountId: 'stale', displayName: 'Stale' }));
    const { result } = renderHook(() => useSelectedProfile());

    act(() => {
      result.current.selectBand({
        bandId: 'band-1',
        bandType: 'Band_Duets',
        teamKey: 'p1:p2',
        displayName: '',
        members: [
          { accountId: 'p1', displayName: 'Player One' },
          { accountId: 'p2', displayName: '' },
        ],
      });
    });

    expect(result.current.profile).toEqual({
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'Unknown Band',
      members: [
        { accountId: 'p1', displayName: 'Player One' },
        { accountId: 'p2', displayName: 'Unknown User' },
      ],
    });
    expect(localStorage.getItem(LEGACY_PLAYER_STORAGE_KEY)).toBeNull();
  });

  it('keeps old band profiles without members compatible', () => {
    localStorage.setItem(SELECTED_PROFILE_STORAGE_KEY, JSON.stringify({
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'Old Band',
    }));

    const { result } = renderHook(() => useSelectedProfile());

    expect(result.current.profile).toEqual({
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'Old Band',
      members: [],
    });
  });

  it('clears both selected profile and legacy player storage', () => {
    localStorage.setItem(SELECTED_PROFILE_STORAGE_KEY, JSON.stringify({ type: 'player', accountId: 'p1', displayName: 'Player One' }));
    localStorage.setItem(LEGACY_PLAYER_STORAGE_KEY, JSON.stringify({ accountId: 'p1', displayName: 'Player One' }));
    const { result } = renderHook(() => useSelectedProfile());

    act(() => {
      result.current.clearSelectedProfile();
    });

    expect(result.current.profile).toBeNull();
    expect(localStorage.getItem(SELECTED_PROFILE_STORAGE_KEY)).toBeNull();
    expect(localStorage.getItem(LEGACY_PLAYER_STORAGE_KEY)).toBeNull();
  });

  it('clears mirrored player selection when the legacy player key is removed', () => {
    localStorage.setItem(SELECTED_PROFILE_STORAGE_KEY, JSON.stringify({ type: 'player', accountId: 'p1', displayName: 'Player One' }));
    localStorage.setItem(LEGACY_PLAYER_STORAGE_KEY, JSON.stringify({ accountId: 'p1', displayName: 'Player One' }));

    const { result } = renderHook(() => useSelectedProfile());

    act(() => {
      localStorage.removeItem(LEGACY_PLAYER_STORAGE_KEY);
      window.dispatchEvent(new Event('fst:trackedPlayerChanged'));
    });

    expect(result.current.profile).toBeNull();
    expect(localStorage.getItem(SELECTED_PROFILE_STORAGE_KEY)).toBeNull();
  });
});