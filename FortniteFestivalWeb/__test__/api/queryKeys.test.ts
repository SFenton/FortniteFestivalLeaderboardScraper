import { describe, it, expect } from 'vitest';
import { queryKeys } from '../../src/api/queryKeys';

describe('queryKeys', () => {
  it('features() returns ["features"]', () => {
    expect(queryKeys.features()).toEqual(['features']);
  });

  it('serviceInfo() returns a profile-independent key', () => {
    expect(queryKeys.serviceInfo()).toEqual(['serviceInfo']);
  });

  it('songs() returns ["songs"]', () => {
    expect(queryKeys.songs()).toEqual(['songs']);
  });

  it('player() returns key with accountId and optional params', () => {
    expect(queryKeys.player('acc-1')).toEqual(['player', 'acc-1', { songId: undefined, instruments: undefined, leeway: undefined }]);
    expect(queryKeys.player('acc-1', 'song-1', ['Solo_Guitar'])).toEqual(['player', 'acc-1', { songId: 'song-1', instruments: ['Solo_Guitar'], leeway: undefined }]);
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

  it('memberScoreFilter() returns key with member conditions and instruments', () => {
    expect(queryKeys.memberScoreFilter(['acct-1'], ['acct-2'], ['Solo_Guitar', 'Solo_Bass'], 1.5)).toEqual([
      'memberScoreFilter',
      { hasAccountIds: ['acct-1'], missingAccountIds: ['acct-2'], instruments: ['Solo_Guitar', 'Solo_Bass'], leeway: 1.5 },
    ]);
  });

  it('songBandLeaderboard() returns key with selected band and combo params', () => {
    expect(queryKeys.songBandLeaderboard('song-1', 'Band_Duets', 25, 0, 'acct-1', 'acct-1:acct-2', 'Solo_Guitar+Solo_Bass')).toEqual([
      'songBandLeaderboard',
      'song-1',
      'Band_Duets',
      { top: 25, offset: 0, selectedAccountId: 'acct-1', selectedTeamKey: 'acct-1:acct-2', comboId: 'Solo_Guitar+Solo_Bass' },
    ]);
  });

  it('allSongBandLeaderboards() returns key with selected band and combo params', () => {
    expect(queryKeys.allSongBandLeaderboards('song-1', 10, undefined, 'Band_Duets', 'acct-1:acct-2', 'Solo_Guitar+Solo_Bass')).toEqual([
      'allSongBandLeaderboards',
      'song-1',
      { top: 10, selectedAccountId: undefined, selectedBandType: 'Band_Duets', selectedTeamKey: 'acct-1:acct-2', comboId: 'Solo_Guitar+Solo_Bass' },
    ]);
  });

  it('playerStats() returns key with accountId', () => {
    expect(queryKeys.playerStats('acc-1')).toEqual(['playerStats', 'acc-1']);
  });

  it('playerBandsList() returns key with accountId, group, and pagination', () => {
    expect(queryKeys.playerBandsList('acc-1', 'duos', 2, 25)).toEqual(['playerBandsList', 'acc-1', { group: 'duos', page: 2, pageSize: 25 }]);
  });

  it('bandRankHistory() returns key with band identity, days, and combo', () => {
    expect(queryKeys.bandRankHistory('Band_Duets', 'p1:p2', 30, 'Solo_Guitar+Solo_Bass')).toEqual([
      'bandRankHistory',
      'Band_Duets',
      'p1:p2',
      { days: 30, comboId: 'Solo_Guitar+Solo_Bass' },
    ]);
  });

  it('bandSongs() returns key with band identity, limit, and combo', () => {
    expect(queryKeys.bandSongs('Band_Duets', 'p1:p2', 5, 'Solo_Guitar+Solo_Bass')).toEqual([
      'bandSongs',
      'Band_Duets',
      'p1:p2',
      { limit: 5, comboId: 'Solo_Guitar+Solo_Bass' },
    ]);
  });

  it('version() returns ["version"]', () => {
    expect(queryKeys.version()).toEqual(['version']);
  });

  it('rivalsOverview() returns key with accountId', () => {
    expect(queryKeys.rivalsOverview('acc-1')).toEqual(['rivals', 'acc-1', 'overview']);
  });

  it('rivalsList() returns key with accountId and combo', () => {
    expect(queryKeys.rivalsList('acc-1', 'Solo_Guitar')).toEqual(['rivals', 'acc-1', 'list', 'Solo_Guitar']);
  });

  it('rivalDetail() returns a canonical profile and scope key', () => {
    expect(queryKeys.rivalDetail('acc-1', 'rival-1', {
      source: 'song',
      scopes: ['Solo_Guitar', 'Solo_Bass'],
      allowLiveFallback: true,
    })).toEqual(
      ['rivals', 'acc-1', 'detail', 'rival-1', {
        source: 'song',
        scopes: ['Solo_Bass', 'Solo_Guitar'],
        allowLiveFallback: true,
      }],
    );
  });

  it('leaderboardRivals() scopes instrument data to the selected profile and metric', () => {
    expect(queryKeys.leaderboardRivals('acc-1', 'Solo_Guitar', 'totalscore')).toEqual(
      ['rivals', 'acc-1', 'leaderboard', 'Solo_Guitar', { rankBy: 'totalscore' }],
    );
  });
});
