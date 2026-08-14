import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  PROFILE_SYNC_STATUS_POLL_MS,
  useSelectedProfileSyncStatus,
} from '../../../src/pages/settings/useSelectedProfileSyncStatus';
import type { SelectedProfile } from '../../../src/hooks/data/useSelectedProfile';

const mockApi = vi.hoisted(() => ({
  getSyncStatus: vi.fn(),
  getBandSyncStatus: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

describe('useSelectedProfileSyncStatus', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mockApi.getSyncStatus.mockResolvedValue({
      accountId: 'player-1',
      isTracked: true,
      backfill: null,
      historyRecon: null,
    });
    mockApi.getBandSyncStatus.mockResolvedValue({
      bandType: 'Band_Duets',
      teamKey: 'player-1:player-2',
      members: [],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it('loads and polls the selected player status', async () => {
    const { result } = renderHook(() => useSelectedProfileSyncStatus({
      type: 'player',
      accountId: 'player-1',
      displayName: 'Player One',
    }));

    await waitFor(() => expect(result.current.playerStatus).not.toBeNull());
    const callsBeforePoll = mockApi.getSyncStatus.mock.calls.length;
    expect(callsBeforePoll).toBeGreaterThan(0);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(PROFILE_SYNC_STATUS_POLL_MS);
    });
    expect(mockApi.getSyncStatus.mock.calls.length).toBeGreaterThan(callsBeforePoll);
  });

  it('loads the selected band status and cancels on unmount', async () => {
    const { result, unmount } = renderHook(() => useSelectedProfileSyncStatus({
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'player-1:player-2',
      displayName: 'Player One + Player Two',
      members: [],
    }));

    await waitFor(() => expect(result.current.bandStatus).not.toBeNull());
    const signal = mockApi.getBandSyncStatus.mock.calls[0]?.[2]?.signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
  });

  it('clears state without a selection and exposes failures', async () => {
    mockApi.getSyncStatus.mockRejectedValueOnce(new Error('offline'));
    const { result, rerender } = renderHook(
      ({ selected }) => useSelectedProfileSyncStatus(selected),
      {
        initialProps: {
          selected: {
            type: 'player',
            accountId: 'player-1',
            displayName: 'Player One',
          } as SelectedProfile | null,
        },
      },
    );

    await waitFor(() => expect(result.current.loadFailed).toBe(true));
    rerender({ selected: null });
    expect(result.current).toEqual({
      playerStatus: null,
      bandStatus: null,
      loadFailed: false,
    });
  });
});
