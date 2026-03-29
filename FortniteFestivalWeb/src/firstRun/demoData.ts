/**
 * Static mock data shared across first-run demo components.
 * Uses hardcoded values so demos render without API calls or auth.
 */
import type { RivalSummary } from '@festival/core/api/serverTypes';

/* ── Leaderboard entries ── */

export type DemoRankingEntry = {
  rank: number;
  displayName: string;
  ratingLabel: string;
  isPlayer?: boolean;
};

export const DEMO_RANKINGS: DemoRankingEntry[] = [
  { rank: 1, displayName: 'guitarmaster99', ratingLabel: '2,480,000' },
  { rank: 2, displayName: 'festival_pro', ratingLabel: '2,310,500' },
  { rank: 3, displayName: 'drumsOnFire', ratingLabel: '2,275,100' },
  { rank: 4, displayName: 'vocalQueen', ratingLabel: '2,198,000' },
  { rank: 5, displayName: 'bassDropKing', ratingLabel: '2,112,800' },
  { rank: 6, displayName: 'topClutch_fn', ratingLabel: '2,045,300' },
  { rank: 7, displayName: 'stageReady', ratingLabel: '1,998,700' },
  { rank: 8, displayName: 'rhythm_sniper', ratingLabel: '1,922,400' },
  { rank: 9, displayName: 'epicShred42', ratingLabel: '1,874,600' },
  { rank: 10, displayName: 'fcOrBust', ratingLabel: '1,801,200' },
];

export const DEMO_PLAYER_ENTRY: DemoRankingEntry = {
  rank: 42,
  displayName: 'You',
  ratingLabel: '1,250,000',
  isPlayer: true,
};

/** Entries surrounding the player (ranks 39-45) for the "Your Rank" demo. */
export const DEMO_NEIGHBORHOOD: DemoRankingEntry[] = [
  { rank: 39, displayName: 'sonicBloom', ratingLabel: '1,278,400' },
  { rank: 40, displayName: 'bassline_blitz', ratingLabel: '1,265,100' },
  { rank: 41, displayName: 'keysAndDreams', ratingLabel: '1,258,000' },
  { rank: 42, displayName: 'You', ratingLabel: '1,250,000', isPlayer: true },
  { rank: 43, displayName: 'drumRollPlz', ratingLabel: '1,241,300' },
  { rank: 44, displayName: 'shredVanHalen', ratingLabel: '1,230,800' },
  { rank: 45, displayName: 'noteFrenzy', ratingLabel: '1,219,500' },
];

/* ── Metric variants ── */

/* ── Rivals ── */

/** Large pool of "above" rivals for rotation in demos. */
export const DEMO_RIVALS_ABOVE: RivalSummary[] = [
  { accountId: 'demo-above-1', displayName: 'keysAndDreams', rivalScore: 920, sharedSongCount: 148, aheadCount: 82, behindCount: 66, avgSignedDelta: 12 },
  { accountId: 'demo-above-2', displayName: 'bassline_blitz', rivalScore: 870, sharedSongCount: 135, aheadCount: 75, behindCount: 60, avgSignedDelta: 8 },
  { accountId: 'demo-above-3', displayName: 'sonicBloom', rivalScore: 840, sharedSongCount: 120, aheadCount: 68, behindCount: 52, avgSignedDelta: 5 },
  { accountId: 'demo-above-4', displayName: 'proGuitarHero', rivalScore: 900, sharedSongCount: 155, aheadCount: 88, behindCount: 67, avgSignedDelta: 10 },
  { accountId: 'demo-above-5', displayName: 'neonStrum', rivalScore: 855, sharedSongCount: 122, aheadCount: 70, behindCount: 52, avgSignedDelta: 7 },
  { accountId: 'demo-above-6', displayName: 'beatMachineX', rivalScore: 830, sharedSongCount: 118, aheadCount: 65, behindCount: 53, avgSignedDelta: 4 },
];

/** Large pool of "below" rivals for rotation in demos. */
export const DEMO_RIVALS_BELOW: RivalSummary[] = [
  { accountId: 'demo-below-1', displayName: 'drumRollPlz', rivalScore: 790, sharedSongCount: 142, aheadCount: 58, behindCount: 84, avgSignedDelta: -10 },
  { accountId: 'demo-below-2', displayName: 'shredVanHalen', rivalScore: 750, sharedSongCount: 130, aheadCount: 50, behindCount: 80, avgSignedDelta: -14 },
  { accountId: 'demo-below-3', displayName: 'noteFrenzy', rivalScore: 710, sharedSongCount: 118, aheadCount: 44, behindCount: 74, avgSignedDelta: -18 },
  { accountId: 'demo-below-4', displayName: 'axelRose_fn', rivalScore: 770, sharedSongCount: 138, aheadCount: 54, behindCount: 84, avgSignedDelta: -12 },
  { accountId: 'demo-below-5', displayName: 'lowEndTheo', rivalScore: 730, sharedSongCount: 126, aheadCount: 46, behindCount: 80, avgSignedDelta: -16 },
  { accountId: 'demo-below-6', displayName: 'offbeatOllie', rivalScore: 695, sharedSongCount: 112, aheadCount: 40, behindCount: 72, avgSignedDelta: -20 },
];

/** Per-instrument rival pools for rotation in the instruments demo. */
export const DEMO_INSTRUMENT_RIVALS: Record<string, { above: RivalSummary[]; below: RivalSummary[] }> = {
  Solo_Guitar: {
    above: [
      { accountId: 'ig-1', displayName: 'stageReady', rivalScore: 860, sharedSongCount: 140, aheadCount: 78, behindCount: 62, avgSignedDelta: 6 },
      { accountId: 'ig-3', displayName: 'proGuitarHero', rivalScore: 890, sharedSongCount: 145, aheadCount: 82, behindCount: 63, avgSignedDelta: 9 },
      { accountId: 'ig-5', displayName: 'neonStrum', rivalScore: 845, sharedSongCount: 130, aheadCount: 72, behindCount: 58, avgSignedDelta: 5 },
    ],
    below: [
      { accountId: 'ig-2', displayName: 'epicShred42', rivalScore: 720, sharedSongCount: 125, aheadCount: 48, behindCount: 77, avgSignedDelta: -12 },
      { accountId: 'ig-4', displayName: 'axelRose_fn', rivalScore: 700, sharedSongCount: 118, aheadCount: 42, behindCount: 76, avgSignedDelta: -15 },
      { accountId: 'ig-6', displayName: 'lowEndTheo', rivalScore: 680, sharedSongCount: 110, aheadCount: 38, behindCount: 72, avgSignedDelta: -18 },
    ],
  },
  Solo_Drums: {
    above: [
      { accountId: 'id-1', displayName: 'drumsOnFire', rivalScore: 910, sharedSongCount: 132, aheadCount: 80, behindCount: 52, avgSignedDelta: 14 },
      { accountId: 'id-3', displayName: 'beatMachineX', rivalScore: 875, sharedSongCount: 128, aheadCount: 74, behindCount: 54, avgSignedDelta: 10 },
      { accountId: 'id-5', displayName: 'doubleKick99', rivalScore: 850, sharedSongCount: 120, aheadCount: 70, behindCount: 50, avgSignedDelta: 8 },
    ],
    below: [
      { accountId: 'id-2', displayName: 'rhythm_sniper', rivalScore: 680, sharedSongCount: 115, aheadCount: 40, behindCount: 75, avgSignedDelta: -20 },
      { accountId: 'id-4', displayName: 'offbeatOllie', rivalScore: 660, sharedSongCount: 108, aheadCount: 36, behindCount: 72, avgSignedDelta: -22 },
      { accountId: 'id-6', displayName: 'drumRollPlz', rivalScore: 640, sharedSongCount: 100, aheadCount: 32, behindCount: 68, avgSignedDelta: -24 },
    ],
  },
  Solo_Vocals: {
    above: [
      { accountId: 'iv-1', displayName: 'vocalQueen', rivalScore: 880, sharedSongCount: 128, aheadCount: 74, behindCount: 54, avgSignedDelta: 10 },
      { accountId: 'iv-3', displayName: 'sonicBloom', rivalScore: 860, sharedSongCount: 122, aheadCount: 70, behindCount: 52, avgSignedDelta: 8 },
      { accountId: 'iv-5', displayName: 'festival_pro', rivalScore: 840, sharedSongCount: 116, aheadCount: 66, behindCount: 50, avgSignedDelta: 6 },
    ],
    below: [
      { accountId: 'iv-2', displayName: 'topClutch_fn', rivalScore: 700, sharedSongCount: 110, aheadCount: 42, behindCount: 68, avgSignedDelta: -16 },
      { accountId: 'iv-4', displayName: 'noteFrenzy', rivalScore: 680, sharedSongCount: 104, aheadCount: 38, behindCount: 66, avgSignedDelta: -18 },
      { accountId: 'iv-6', displayName: 'keysAndDreams', rivalScore: 660, sharedSongCount: 98, aheadCount: 34, behindCount: 64, avgSignedDelta: -20 },
    ],
  },
};
