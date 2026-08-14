import { describe, expect, it, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { playerHistoryQueryOptions, rivalsAllQueryOptions } from '../../src/api/remoteDataQueries';

const mockApi = vi.hoisted(() => ({
  getPlayerHistory: vi.fn(),
  getRivalsAll: vi.fn(),
}));

vi.mock('../../src/api/client', () => ({ api: mockApi }));

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
    },
  });
}

describe('remoteDataQueries', () => {
  it('deduplicates concurrent player history consumers', async () => {
    let resolveHistory!: (value: { history: Array<{ instrument: string }> }) => void;
    mockApi.getPlayerHistory.mockImplementationOnce(() => (
      new Promise(resolve => {
        resolveHistory = resolve;
      })
    ));
    const queryClient = createQueryClient();
    const options = playerHistoryQueryOptions('player-1', 'song-1');

    const first = queryClient.fetchQuery(options);
    const second = queryClient.fetchQuery(options);

    expect(mockApi.getPlayerHistory).toHaveBeenCalledTimes(1);
    resolveHistory({ history: [{ instrument: 'Solo_Guitar' }] });

    await expect(Promise.all([first, second])).resolves.toEqual([
      [{ instrument: 'Solo_Guitar' }],
      [{ instrument: 'Solo_Guitar' }],
    ]);
  });

  it('keeps rivals-all data account scoped and reusable', async () => {
    mockApi.getRivalsAll.mockResolvedValueOnce({
      accountId: 'player-1',
      songs: [],
      combos: [],
    });
    const queryClient = createQueryClient();
    const options = rivalsAllQueryOptions('player-1');

    await queryClient.fetchQuery(options);
    await queryClient.fetchQuery(options);

    expect(options.queryKey).toEqual(['rivals', 'player-1', 'all']);
    expect(mockApi.getRivalsAll).toHaveBeenCalledTimes(1);
  });
});
