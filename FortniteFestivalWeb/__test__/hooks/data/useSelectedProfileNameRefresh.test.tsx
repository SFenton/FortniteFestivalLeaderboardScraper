import React from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../../../src/api/client';
import {
  getSelectedProfileRefreshAccountIds,
  getSelectedProfileRefreshKey,
  patchDisplayNamesInValue,
  useSelectedProfileNameRefresh,
} from '../../../src/hooks/data/useSelectedProfileNameRefresh';
import {
  readSelectedProfile,
  writeSelectedProfile,
  type SelectedProfile,
} from '../../../src/state/selectedProfile';

vi.mock('../../../src/api/client', () => ({
  api: {
    refreshAccountNames: vi.fn(),
  },
}));

const refreshAccountNames = vi.mocked(api.refreshAccountNames);

function createWrapper(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  };
}

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

beforeEach(() => {
  localStorage.clear();
  vi.useFakeTimers();
  refreshAccountNames.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('useSelectedProfileNameRefresh', () => {
  it('silently refreshes a selected player and patches selected profile plus query cache', async () => {
    const queryClient = createQueryClient();
    const profile: SelectedProfile = { type: 'player', accountId: 'acct1', displayName: 'OldName' };
    writeSelectedProfile(profile);
    queryClient.setQueryData(['player', 'acct1'], { accountId: 'acct1', displayName: 'OldName', totalSongs: 3 });
    refreshAccountNames.mockResolvedValue({
      changed: 1,
      unchanged: 0,
      failed: 0,
      missing: 0,
      names: { acct1: 'NewName' },
      changedAccountIds: ['acct1'],
    });

    renderHook(() => useSelectedProfileNameRefresh(profile), { wrapper: createWrapper(queryClient) });

    expect(refreshAccountNames).not.toHaveBeenCalled();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(200);
      await Promise.resolve();
    });

    expect(refreshAccountNames).toHaveBeenCalledWith(['acct1']);
    expect(readSelectedProfile()).toEqual({ type: 'player', accountId: 'acct1', displayName: 'NewName' });
    expect(queryClient.getQueryData(['player', 'acct1'])).toEqual({ accountId: 'acct1', displayName: 'NewName', totalSongs: 3 });
  });

  it('refreshes each selected band member once and preserves member-derived band names', async () => {
    const queryClient = createQueryClient();
    const profile: SelectedProfile = {
      type: 'band',
      bandId: 'band1',
      bandType: 'Band_Duets',
      teamKey: 'acct1:acct2',
      displayName: 'Old One + Old Two + Old One',
      members: [
        { accountId: 'acct1', displayName: 'Old One' },
        { accountId: 'acct2', displayName: 'Old Two' },
        { accountId: 'acct1', displayName: 'Old One' },
      ],
    };
    writeSelectedProfile(profile);
    refreshAccountNames.mockResolvedValue({
      changed: 2,
      unchanged: 0,
      failed: 0,
      missing: 0,
      names: { acct1: 'New One', acct2: 'New Two' },
      changedAccountIds: ['acct1', 'acct2'],
    });

    renderHook(() => useSelectedProfileNameRefresh(profile), { wrapper: createWrapper(queryClient) });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(200);
      await Promise.resolve();
    });

    expect(refreshAccountNames).toHaveBeenCalledWith(['acct1', 'acct2']);
    expect(readSelectedProfile()).toEqual({
      ...profile,
      displayName: 'New One + New Two + New One',
      members: [
        { accountId: 'acct1', displayName: 'New One' },
        { accountId: 'acct2', displayName: 'New Two' },
        { accountId: 'acct1', displayName: 'New One' },
      ],
    });
  });

  it('ignores a late response after the selected profile changes', async () => {
    const queryClient = createQueryClient();
    const firstProfile: SelectedProfile = { type: 'player', accountId: 'acct1', displayName: 'Old One' };
    const secondProfile: SelectedProfile = { type: 'player', accountId: 'acct2', displayName: 'Old Two' };
    writeSelectedProfile(firstProfile);

    let resolveFirst: (value: Awaited<ReturnType<typeof api.refreshAccountNames>>) => void = () => undefined;
    refreshAccountNames
      .mockReturnValueOnce(new Promise(resolve => { resolveFirst = resolve; }))
      .mockResolvedValueOnce({
        changed: 0,
        unchanged: 1,
        failed: 0,
        missing: 0,
        names: {},
        changedAccountIds: [],
      });

    const { rerender } = renderHook(
      ({ profile }) => useSelectedProfileNameRefresh(profile),
      { initialProps: { profile: firstProfile }, wrapper: createWrapper(queryClient) },
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(200);
    });
    writeSelectedProfile(secondProfile);
    rerender({ profile: secondProfile });

    await act(async () => {
      resolveFirst({
        changed: 1,
        unchanged: 0,
        failed: 0,
        missing: 0,
        names: { acct1: 'Late Name' },
        changedAccountIds: ['acct1'],
      });
      await Promise.resolve();
    });

    expect(readSelectedProfile()).toEqual(secondProfile);
  });
});

describe('selected profile name refresh helpers', () => {
  it('derives stable account IDs and refresh keys from profile identity only', () => {
    const profile: SelectedProfile = {
      type: 'band',
      bandId: 'band1',
      bandType: 'Band_Duets',
      teamKey: 'b:a',
      displayName: 'Original Names',
      members: [
        { accountId: 'acct2', displayName: 'Two' },
        { accountId: 'acct1', displayName: 'One' },
        { accountId: 'acct2', displayName: 'Two' },
      ],
    };

    expect(getSelectedProfileRefreshAccountIds(profile)).toEqual(['acct2', 'acct1']);
    expect(getSelectedProfileRefreshKey(profile)).toBe('band:band1:Band_Duets:b:a:acct1,acct2');
  });

  it('patches nested account display names case-insensitively', () => {
    const value = {
      entries: [
        { accountId: 'ACCT1', displayName: 'Old', rank: 1 },
        { members: [{ accountId: 'acct2', displayName: 'Two' }] },
      ],
    };

    expect(patchDisplayNamesInValue(value, { acct1: 'New', ACCT2: 'New Two' })).toEqual({
      entries: [
        { accountId: 'ACCT1', displayName: 'New', rank: 1 },
        { members: [{ accountId: 'acct2', displayName: 'New Two' }] },
      ],
    });
  });
});