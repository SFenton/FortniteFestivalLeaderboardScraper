import { describe, expect, it } from 'vitest';
import type {
  PlayerScore,
  ServerInstrumentKey as InstrumentKey,
  ServerSong as Song,
} from '@festival/core/api';
import { buildSongQuickLinkSections } from '../../../src/pages/songs/songQuickLinks';

const instrument: InstrumentKey = 'Solo_PeripheralGuitar';
const song = {
  songId: 'missing-max-song',
  title: 'Missing Max',
  artist: 'Test',
  maxScores: {},
} as Song;
const score = {
  songId: song.songId,
  instrument,
  score: 100_000,
} as PlayerScore;
const t = (key: string) => key;

function sectionId(sortMode: 'maxdistance' | 'maxscorediff', includeScore: boolean): string {
  const result = buildSongQuickLinkSections({
    songs: [song],
    sortMode,
    instrument,
    scoreMap: includeScore ? new Map([[song.songId, score]]) : new Map(),
    allScoreMap: new Map(),
    t,
  });
  return result.sections[0]!.id;
}

describe('max-score quick-link buckets', () => {
  it.each(['maxdistance', 'maxscorediff'] as const)(
    'distinguishes a missing max score from no player score for %s',
    (sortMode) => {
      expect(sectionId(sortMode, true)).toBe(`${sortMode}:max-unavailable`);
      expect(sectionId(sortMode, false)).toBe(`${sortMode}:no-score`);
    },
  );
});
