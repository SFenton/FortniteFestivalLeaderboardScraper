import {ScoreTracker} from '../../models';
import type {LeaderboardData} from '../../models';
import {buildScoreRows} from '../scoreRows';

describe('scoreRows extended coverage', () => {
  const mkBoard = (songId: string, title: string, artist: string, instrument: string, opts: Partial<ScoreTracker> = {}): LeaderboardData => {
    const t = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 1000, percentHit: 900000, numStars: 5, isFullCombo: false, seasonAchieved: 2, ...opts});
    return {songId, title, artist, [instrument]: t} as any;
  };

  test('buildScoreRows sorts by Artist column', () => {
    const scores = [mkBoard('b', 'B', 'Zebra', 'guitar'), mkBoard('a', 'A', 'Alpha', 'guitar')];
    const rows = buildScoreRows({scores, instrument: 'Lead', sortColumn: 'Artist'});
    expect(rows.map(r => r.artist)).toEqual(['Alpha', 'Zebra']);
  });

  test('buildScoreRows Artist tiebreak falls through to title', () => {
    const scores = [mkBoard('b', 'Zebra', 'Same', 'guitar'), mkBoard('a', 'Alpha', 'Same', 'guitar')];
    const rows = buildScoreRows({scores, instrument: 'Lead', sortColumn: 'Artist'});
    expect(rows.map(r => r.title)).toEqual(['Alpha', 'Zebra']);
  });

  test('buildScoreRows sorts by Score column', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar', {maxScore: 500}), mkBoard('b', 'B', 'Y', 'guitar', {maxScore: 1000})];
    const rows = buildScoreRows({scores, instrument: 'Lead', sortColumn: 'Score'});
    expect(rows.map(r => r.score)).toEqual([500, 1000]);
  });

  test('buildScoreRows sorts by Percent column', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar', {percentHit: 500000}), mkBoard('b', 'B', 'Y', 'guitar', {percentHit: 900000})];
    const rows = buildScoreRows({scores, instrument: 'Lead', sortColumn: 'Percent'});
    expect(rows[0].songId).toBe('a');
    expect(rows[1].songId).toBe('b');
  });

  test('buildScoreRows sorts by Stars column', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar', {numStars: 3}), mkBoard('b', 'B', 'Y', 'guitar', {numStars: 6})];
    const rows = buildScoreRows({scores, instrument: 'Lead', sortColumn: 'Stars'});
    expect(rows.map(r => r.starText)).toEqual(['***', '******']);
  });

  test('buildScoreRows sorts by FC column', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar', {isFullCombo: false}), mkBoard('b', 'B', 'Y', 'guitar', {isFullCombo: true})];
    const rows = buildScoreRows({scores, instrument: 'Lead', sortColumn: 'FC'});
    expect(rows.map(r => r.isFullCombo)).toEqual([false, true]);
  });

  test('buildScoreRows sorts by Season column', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar', {seasonAchieved: 5}), mkBoard('b', 'B', 'Y', 'guitar', {seasonAchieved: 1})];
    const rows = buildScoreRows({scores, instrument: 'Lead', sortColumn: 'Season'});
    expect(rows.map(r => r.season)).toEqual(['1', '5']);
  });

  test('buildScoreRows reverses with sortDesc', () => {
    const scores = [mkBoard('a', 'A', 'Alpha', 'guitar'), mkBoard('b', 'B', 'Zebra', 'guitar')];
    const rows = buildScoreRows({scores, instrument: 'Lead', sortColumn: 'Title', sortDesc: true});
    expect(rows.map(r => r.title)).toEqual(['B', 'A']);
  });

  test('buildScoreRows filters by text', () => {
    const scores = [mkBoard('a', 'Hello World', 'Artist', 'guitar'), mkBoard('b', 'Goodbye', 'Moon', 'guitar')];
    const rows = buildScoreRows({scores, instrument: 'Lead', filterText: 'hello'});
    expect(rows.length).toBe(1);
    expect(rows[0].title).toBe('Hello World');
  });

  test('buildScoreRows filters by artist text', () => {
    const scores = [mkBoard('a', 'Song', 'Alpha', 'guitar'), mkBoard('b', 'Song', 'Beta', 'guitar')];
    const rows = buildScoreRows({scores, instrument: 'Lead', filterText: 'beta'});
    expect(rows.length).toBe(1);
  });

  test('buildScoreRows maps Drums instrument', () => {
    const scores = [mkBoard('a', 'A', 'X', 'drums')];
    const rows = buildScoreRows({scores, instrument: 'Drums'});
    expect(rows.length).toBe(1);
  });

  test('buildScoreRows maps Vocals instrument', () => {
    const scores = [mkBoard('a', 'A', 'X', 'vocals')];
    const rows = buildScoreRows({scores, instrument: 'Vocals'});
    expect(rows.length).toBe(1);
  });

  test('buildScoreRows maps Bass instrument', () => {
    const scores = [mkBoard('a', 'A', 'X', 'bass')];
    const rows = buildScoreRows({scores, instrument: 'Bass'});
    expect(rows.length).toBe(1);
  });

  test('buildScoreRows maps ProLead instrument', () => {
    const scores = [mkBoard('a', 'A', 'X', 'pro_guitar')];
    const rows = buildScoreRows({scores, instrument: 'ProLead'});
    expect(rows.length).toBe(1);
  });

  test('buildScoreRows maps ProBass instrument', () => {
    const scores = [mkBoard('a', 'A', 'X', 'pro_bass')];
    const rows = buildScoreRows({scores, instrument: 'ProBass'});
    expect(rows.length).toBe(1);
  });

  test('buildScoreRows shows All-Time for seasonAchieved <= 0', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar', {seasonAchieved: 0})];
    const rows = buildScoreRows({scores, instrument: 'Lead'});
    expect(rows[0].season).toBe('All-Time');
  });

  test('buildScoreRows excludes entries without initialized tracker', () => {
    const t = new ScoreTracker(); // initialized=false by default
    const scores: LeaderboardData[] = [{songId: 'a', title: 'A', artist: 'X', guitar: t}];
    const rows = buildScoreRows({scores, instrument: 'Lead'});
    expect(rows.length).toBe(0);
  });

  test('buildScoreRows shows FC symbol for full combo', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar', {isFullCombo: true})];
    const rows = buildScoreRows({scores, instrument: 'Lead'});
    expect(rows[0].fullComboSymbol).toBe('FC');
  });

  test('buildScoreRows star capping at 6', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar', {numStars: 6})];
    const rows = buildScoreRows({scores, instrument: 'Lead'});
    expect(rows[0].maxStars).toBe(true);
    expect(rows[0].starText).toBe('******');
  });

  test('buildScoreRows zero stars shows empty', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar', {numStars: 0})];
    const rows = buildScoreRows({scores, instrument: 'Lead'});
    expect(rows[0].starText).toBe('');
    expect(rows[0].maxStars).toBe(false);
  });

  test('buildScoreRows default sort is Title', () => {
    const scores = [mkBoard('b', 'Banana', 'X', 'guitar'), mkBoard('a', 'Apple', 'Y', 'guitar')];
    const rows = buildScoreRows({scores, instrument: 'Lead'});
    expect(rows.map(r => r.title)).toEqual(['Apple', 'Banana']);
  });

  test('buildScoreRows handles undefined title and artist gracefully', () => {
    const t = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 500000, numStars: 3, isFullCombo: false, seasonAchieved: 0});
    const board: LeaderboardData = {songId: 'x', guitar: t};
    // title and artist are undefined
    const rows = buildScoreRows({scores: [board], instrument: 'Lead'});
    expect(rows[0].title).toBe('');
    expect(rows[0].artist).toBe('');
    expect(rows[0].songId).toBe('x');
  });

  test('buildScoreRows filters with undefined title/artist boards', () => {
    const t = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 500000, numStars: 3, isFullCombo: false});
    const board: LeaderboardData = {songId: 'x', guitar: t}; // no title or artist
    const rows = buildScoreRows({scores: [board], instrument: 'Lead', filterText: 'test'});
    expect(rows.length).toBe(0); // should not match empty strings
  });

  test('buildScoreRows Artist sort with undefined artist falls back to empty string comparison', () => {
    const t1 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 500000, numStars: 3, isFullCombo: false});
    const t2 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 200, percentHit: 600000, numStars: 4, isFullCombo: false});
    const boards: LeaderboardData[] = [
      {songId: 'a', title: 'B', guitar: t1},    // artist is undefined
      {songId: 'b', title: 'A', artist: 'Zed', guitar: t2},
    ];
    const rows = buildScoreRows({scores: boards, instrument: 'Lead', sortColumn: 'Artist'});
    // empty string sorts before 'Zed'
    expect(rows[0].artist).toBe('');
    expect(rows[1].artist).toBe('Zed');
  });

  test('buildScoreRows Title sort with undefined title falls back to empty string', () => {
    const t1 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 500000, numStars: 3, isFullCombo: false});
    const t2 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 200, percentHit: 600000, numStars: 4, isFullCombo: false});
    const boards: LeaderboardData[] = [
      {songId: 'a', guitar: t1},    // title is undefined
      {songId: 'b', title: 'Zebra', guitar: t2},
    ];
    const rows = buildScoreRows({scores: boards, instrument: 'Lead', sortColumn: 'Title'});
    // empty string sorts before 'Zebra'
    expect(rows[0].title).toBe('');
    expect(rows[1].title).toBe('Zebra');
  });

  test('buildScoreRows Artist sort with both artists undefined tiebreaks by title', () => {
    const t1 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 500000, numStars: 3, isFullCombo: false});
    const t2 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 200, percentHit: 600000, numStars: 4, isFullCombo: false});
    const boards: LeaderboardData[] = [
      {songId: 'a', title: 'Zebra', guitar: t1},  // artist undefined
      {songId: 'b', title: 'Alpha', guitar: t2},  // artist undefined
    ];
    const rows = buildScoreRows({scores: boards, instrument: 'Lead', sortColumn: 'Artist'});
    // both artists '' → tie → tiebreak by title: 'Alpha' < 'Zebra'
    expect(rows[0].title).toBe('Alpha');
    expect(rows[1].title).toBe('Zebra');
  });

  test('buildScoreRows Artist sort tiebreak with both titles undefined', () => {
    const t1 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 500000, numStars: 3, isFullCombo: false});
    const t2 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 200, percentHit: 600000, numStars: 4, isFullCombo: false});
    const boards: LeaderboardData[] = [
      {songId: 'a', artist: 'Same', guitar: t1},  // title undefined
      {songId: 'b', artist: 'Same', guitar: t2},  // title undefined
    ];
    const rows = buildScoreRows({scores: boards, instrument: 'Lead', sortColumn: 'Artist'});
    // both artists 'Same' → tie → tiebreak by title: both '' → stable
    expect(rows.length).toBe(2);
  });

  test('buildScoreRows songId fallback when undefined', () => {
    const t = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 500000, numStars: 3, isFullCombo: false, seasonAchieved: 0});
    const board = {guitar: t} as any; // songId is undefined
    const rows = buildScoreRows({scores: [board], instrument: 'Lead'});
    expect(rows[0].songId).toBe('');
  });

  test('buildScoreRows filterText with defined title and artist', () => {
    const scores = [mkBoard('a', 'Hello World', 'Artist', 'guitar'), mkBoard('b', 'Goodbye', 'Moon', 'guitar')];
    const rows = buildScoreRows({scores, instrument: 'Lead', filterText: 'ARTIST'});
    // Case-insensitive match on artist
    expect(rows.length).toBe(1);
    expect(rows[0].title).toBe('Hello World');
  });

  test('buildScoreRows filterText empty string shows all', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar'), mkBoard('b', 'B', 'Y', 'guitar')];
    const rows = buildScoreRows({scores, instrument: 'Lead', filterText: ''});
    expect(rows.length).toBe(2);
  });

  test('buildScoreRows filterText whitespace only shows all', () => {
    const scores = [mkBoard('a', 'A', 'X', 'guitar')];
    const rows = buildScoreRows({scores, instrument: 'Lead', filterText: '   '});
    expect(rows.length).toBe(1);
  });

  test('buildScoreRows Title sort with second item having undefined title', () => {
    const t1 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 500000, numStars: 3, isFullCombo: false});
    const t2 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 200, percentHit: 600000, numStars: 4, isFullCombo: false});
    const boards: LeaderboardData[] = [
      {songId: 'a', title: 'Alpha', guitar: t1},  // title defined
      {songId: 'b', guitar: t2},                    // title undefined → ''
    ];
    const rows = buildScoreRows({scores: boards, instrument: 'Lead', sortColumn: 'Title'});
    // '' < 'Alpha' → second item sorts first
    expect(rows[0].title).toBe('');
    expect(rows[1].title).toBe('Alpha');
  });

  test('buildScoreRows Title sort with both titles undefined', () => {
    const t1 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 500000, numStars: 3, isFullCombo: false});
    const t2 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 200, percentHit: 600000, numStars: 4, isFullCombo: false});
    const boards: LeaderboardData[] = [
      {songId: 'a', guitar: t1},
      {songId: 'b', guitar: t2},
    ];
    const rows = buildScoreRows({scores: boards, instrument: 'Lead', sortColumn: 'Title'});
    expect(rows.length).toBe(2);
  });
});
