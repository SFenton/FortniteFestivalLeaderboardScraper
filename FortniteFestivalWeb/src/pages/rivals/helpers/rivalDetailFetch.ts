import type { RivalDetailResponse, RivalSongComparison } from '@festival/core/api/serverTypes';
import { api } from '../../../api/client';

export async function fetchCombinedRivalDetail(
  accountId: string,
  rivalId: string,
  scopes: readonly string[],
  sort?: string,
): Promise<RivalDetailResponse> {
  const uniqueScopes = [...new Set(scopes.filter(Boolean))];
  if (uniqueScopes.length === 0) throw new Error('No rival scopes resolved.');
  if (uniqueScopes.length === 1) return fetchRivalDetail(accountId, uniqueScopes[0]!, rivalId, sort);

  const results = await Promise.allSettled(
    uniqueScopes.map(scope => fetchRivalDetail(accountId, scope, rivalId, sort)),
  );
  const fulfilled = results.filter((result): result is PromiseFulfilledResult<RivalDetailResponse> => result.status === 'fulfilled');
  if (fulfilled.length === 0) {
    const rejected = results.find((result): result is PromiseRejectedResult => result.status === 'rejected');
    throw rejected?.reason ?? new Error('No rival detail scopes returned song data.');
  }

  const first = fulfilled[0]!.value;
  const songs = dedupeSongs(fulfilled.flatMap(result => result.value.songs));
  const displayName = fulfilled.find(result => result.value.rival.displayName)?.value.rival.displayName ?? first.rival.displayName;

  return {
    ...first,
    rival: { ...first.rival, displayName },
    combo: uniqueScopes.join(','),
    totalSongs: songs.length,
    songs,
  };
}

function fetchRivalDetail(accountId: string, scope: string, rivalId: string, sort?: string): Promise<RivalDetailResponse> {
  return sort
    ? api.getRivalDetail(accountId, scope, rivalId, sort)
    : api.getRivalDetail(accountId, scope, rivalId);
}

function dedupeSongs(songs: RivalSongComparison[]): RivalSongComparison[] {
  const seen = new Set<string>();
  return songs.filter(song => {
    const key = `${song.songId}:${song.instrument}:${song.userInstrument ?? ''}:${song.rivalInstrument ?? ''}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}