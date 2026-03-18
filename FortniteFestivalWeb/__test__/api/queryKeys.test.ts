import { describe, it, expect } from 'vitest';
import { queryKeys } from '../../src/api/queryKeys';

describe('queryKeys', () => {
  it('songs() returns ["songs"]', () => {
    expect(queryKeys.songs()).toEqual(['songs']);
  });

  it('player() returns key with accountId and optional params', () => {
    expect(queryKeys.player('acc-1')).toEqual(['player', 'acc-1', { songId: undefined, instruments: undefined }]);
    expect(queryKeys.player('acc-1', 'song-1', ['Solo_Guitar'])).toEqual(['player', 'acc-1', { songId: 'song-1', instruments: ['Solo_Guitar'] }]);
  });

  it('playerHistory() returns key with accountId and optional params', () => {
    expect(queryKeys.playerHistory('acc-1')).toEqual(['playerHistory', 'acc-1', { songId: undefined, instrument: undefined }]);
    expect(queryKeys.playerHistory('acc-1', 'song-1', 'Solo_Guitar')).toEqual(['playerHistory', 'acc-1', { songId: 'song-1', instrument: 'Solo_Guitar' }]);
  });

  it('syncStatus() returns key with accountId', () => {
    expect(queryKeys.syncStatus('acc-1')).toEqual(['syncStatus', 'acc-1']);
  });

  it('leaderboard() returns key with all params', () => {
    expect(queryKeys.leaderboard('song-1', 'Solo_Guitar', 10, 0)).toEqual(['leaderboard', 'song-1', 'Solo_Guitar', { top: 10, offset: 0, leeway: undefined }]);
    expect(queryKeys.leaderboard('song-1', 'Solo_Guitar', 10, 0, 5)).toEqual(['leaderboard', 'song-1', 'Solo_Guitar', { top: 10, offset: 0, leeway: 5 }]);
  });

  it('allLeaderboards() returns key with songId and top', () => {
    expect(queryKeys.allLeaderboards('song-1', 5)).toEqual(['allLeaderboards', 'song-1', { top: 5, leeway: undefined }]);
    expect(queryKeys.allLeaderboards('song-1', 5, 10)).toEqual(['allLeaderboards', 'song-1', { top: 5, leeway: 10 }]);
  });

  it('playerStats() returns key with accountId', () => {
    expect(queryKeys.playerStats('acc-1')).toEqual(['playerStats', 'acc-1']);
  });

  it('version() returns ["version"]', () => {
    expect(queryKeys.version()).toEqual(['version']);
  });
});
