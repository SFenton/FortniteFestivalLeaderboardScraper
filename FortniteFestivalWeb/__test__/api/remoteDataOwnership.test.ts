import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { QueryClient, QueryObserver } from '@tanstack/react-query';
import {
  leaderboardCache,
  songDetailCache,
} from '../../src/api/pageCache';
import { queryKeys } from '../../src/api/queryKeys';
import {
  invalidateLeaderboardData,
  keepPreviousLeaderboardPage,
  keepPreviousSongLeaderboards,
  REMOTE_DATA_GC_TIME_MS,
  remoteDataQueryPolicy,
} from '../../src/api/queryPolicy';

const remoteCacheOwners = [
  'src/api/pageCache.ts',
  'src/pages/rivals/RivalsPage.tsx',
  'src/pages/rivals/AllRivalsPage.tsx',
  'src/pages/rivals/LeaderboardRivalsTab.tsx',
  'src/pages/rivals/RivalDetailPage.tsx',
  'src/pages/rivals/RivalryPage.tsx',
  'src/pages/compete/CompetePage.tsx',
];

function createClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
}

afterEach(() => {
  vi.useRealTimers();
  leaderboardCache.clear();
  songDetailCache.clear();
});

describe('remote data ownership', () => {
  it('keeps page and route module caches free of remote response payloads', () => {
    for (const relativePath of remoteCacheOwners) {
      const source = readFileSync(resolve(process.cwd(), relativePath), 'utf8');
      expect(source, relativePath).not.toMatch(/\b_cached[A-Za-z0-9_]*/);
      expect(source, relativePath).not.toMatch(/Module-level data cache|module-level cache/i);
    }

    const pageCacheSource = readFileSync(resolve(process.cwd(), 'src/api/pageCache.ts'), 'utf8');
    expect(pageCacheSource).not.toMatch(/LeaderboardEntry|ScoreHistory|SongBand|instrumentData|bandData|entries:/);

    const clientSource = readFileSync(resolve(process.cwd(), 'src/api/client.ts'), 'utf8');
    expect(clientSource).not.toMatch(/new Map<.*data|const etagCache/i);
    expect(clientSource).toContain('Shop remains a separate owner until WEB-2.3');
  });

  it('deduplicates concurrent requests for one profile and scope', async () => {
    const client = createClient();
    let resolveRequest: ((value: { combo: string; above: never[]; below: never[] }) => void) | undefined;
    const request = new Promise<{ combo: string; above: never[]; below: never[] }>(resolve => {
      resolveRequest = resolve;
    });
    const queryFn = vi.fn(() => request);
    const options = {
      queryKey: queryKeys.rivalsList('profile-a', 'Solo_Guitar'),
      queryFn,
      ...remoteDataQueryPolicy,
    };

    const first = client.fetchQuery(options);
    const second = client.fetchQuery(options);
    expect(queryFn).toHaveBeenCalledTimes(1);

    resolveRequest?.({ combo: 'Solo_Guitar', above: [], below: [] });
    await expect(Promise.all([first, second])).resolves.toHaveLength(2);
    expect(queryFn).toHaveBeenCalledTimes(1);
    client.clear();
  });

  it('isolates profile data and supports targeted invalidation', async () => {
    const client = createClient();
    const fetchProfileA = vi.fn(async () => ({ profile: 'a' }));
    const fetchProfileB = vi.fn(async () => ({ profile: 'b' }));
    const profileA = {
      queryKey: queryKeys.rivalsList('profile-a', 'Solo_Guitar'),
      queryFn: fetchProfileA,
      ...remoteDataQueryPolicy,
    };
    const profileB = {
      queryKey: queryKeys.rivalsList('profile-b', 'Solo_Guitar'),
      queryFn: fetchProfileB,
      ...remoteDataQueryPolicy,
    };

    await client.fetchQuery(profileA);
    await client.fetchQuery(profileB);
    await client.fetchQuery(profileA);
    expect(fetchProfileA).toHaveBeenCalledTimes(1);
    expect(fetchProfileB).toHaveBeenCalledTimes(1);
    expect(client.getQueryData(profileA.queryKey)).toEqual({ profile: 'a' });
    expect(client.getQueryData(profileB.queryKey)).toEqual({ profile: 'b' });

    await client.invalidateQueries({ queryKey: queryKeys.rivalsScope('profile-a') });
    await client.fetchQuery(profileA);
    expect(fetchProfileA).toHaveBeenCalledTimes(2);
    expect(fetchProfileB).toHaveBeenCalledTimes(1);
    client.clear();
  });

  it('garbage-collects inactive remote entries after the configured idle period', async () => {
    vi.useFakeTimers();
    const client = createClient();
    const key = queryKeys.rivalsList('profile-a', 'Solo_Guitar');

    await client.fetchQuery({
      queryKey: key,
      queryFn: async () => ({ profile: 'a' }),
      ...remoteDataQueryPolicy,
    });
    expect(client.getQueryData(key)).toEqual({ profile: 'a' });

    await vi.advanceTimersByTimeAsync(REMOTE_DATA_GC_TIME_MS + 1);
    expect(client.getQueryData(key)).toBeUndefined();
    client.clear();
  });

  it('keeps a fail-closed error in React Query without refetching on every remount', async () => {
    const client = createClient();
    const queryFn = vi.fn(async () => {
      throw new Error('API 503: Service Unavailable');
    });
    const options = {
      queryKey: queryKeys.rivalsList('profile-a', 'Solo_Guitar'),
      queryFn,
      ...remoteDataQueryPolicy,
    };
    const firstObserver = new QueryObserver(client, options);
    let unsubscribeFirst = () => {};
    await new Promise<void>(resolve => {
      unsubscribeFirst = firstObserver.subscribe(result => {
        if (result.isError) resolve();
      });
    });
    unsubscribeFirst();
    expect(queryFn).toHaveBeenCalledTimes(1);

    const secondObserver = new QueryObserver(client, options);
    const unsubscribeSecond = secondObserver.subscribe(() => {});
    await Promise.resolve();
    expect(secondObserver.getCurrentResult().isError).toBe(true);
    expect(queryFn).toHaveBeenCalledTimes(1);

    unsubscribeSecond();
    client.clear();
  });

  it('preserves navigation state while fresh query data serves back navigation and invalidation refetches', async () => {
    const client = createClient();
    const queryFn = vi.fn(async () => ({
      songId: 'song-1',
      instrument: 'Solo_Guitar',
      count: 1,
      totalEntries: 1,
      localEntries: 1,
      entries: [{ accountId: 'player-1', score: 100, rank: 1 }],
    }));
    const options = {
      queryKey: queryKeys.leaderboard('song-1', 'Solo_Guitar', 25, 25),
      queryFn,
      ...remoteDataQueryPolicy,
    };

    leaderboardCache.set('song-1:Solo_Guitar', { page: 1, scrollTop: 420 });
    songDetailCache.set('song-1', { scrollTop: 275 });
    await client.fetchQuery(options);
    await client.fetchQuery(options);

    expect(queryFn).toHaveBeenCalledTimes(1);
    expect(leaderboardCache.get('song-1:Solo_Guitar')).toEqual({ page: 1, scrollTop: 420 });
    expect(songDetailCache.get('song-1')).toEqual({ scrollTop: 275 });

    await invalidateLeaderboardData(client);
    await client.fetchQuery(options);
    expect(queryFn).toHaveBeenCalledTimes(2);
    client.clear();
  });

  it('keeps placeholder data only within the same song and instrument scope', () => {
    const leaderboard = {
      songId: 'song-1',
      instrument: 'Solo_Guitar',
      count: 0,
      totalEntries: 0,
      localEntries: 0,
      entries: [],
    };
    expect(keepPreviousLeaderboardPage('song-1', 'Solo_Guitar')(leaderboard)).toBe(leaderboard);
    expect(keepPreviousLeaderboardPage('song-2', 'Solo_Guitar')(leaderboard)).toBeUndefined();
    expect(keepPreviousLeaderboardPage('song-1', 'Solo_Bass')(leaderboard)).toBeUndefined();

    const allLeaderboards = { songId: 'song-1', instruments: [] };
    expect(keepPreviousSongLeaderboards('song-1')(allLeaderboards)).toBe(allLeaderboards);
    expect(keepPreviousSongLeaderboards('song-2')(allLeaderboards)).toBeUndefined();
  });
});
