import { describe, it, expect, vi } from 'vitest';
import { render } from '@testing-library/react';
import type { ServerInstrumentKey as InstrumentKey, ServerSong as Song, PlayerScore } from '@festival/core/api/serverTypes';
import { buildTopSongsItems } from '../../../../pages/player/components/TopSongsSection';

vi.mock('../../../../pages/player/sections/PlayerSectionHeading', () => ({
  default: ({ title }: any) => <div data-testid="heading">{title}</div>,
}));
vi.mock('../../../../pages/player/components/PlayerSongRow', () => ({
  default: ({ title, songId }: any) => <div data-testid={`row-${songId}`}>{title}</div>,
}));

const t = (key: string) => key;
const navigateToSongDetail = vi.fn();
const inst: InstrumentKey = 'Solo_Guitar';

function makeSong(id: string): Song {
  return { songId: id, title: `Song ${id}`, artist: 'Artist', year: 2024 };
}

function makeScore(songId: string, rank: number, totalEntries: number): PlayerScore {
  return { songId, instrument: 'Solo_Guitar', score: 100000, rank, totalEntries, accuracy: 95000, isFullCombo: false, stars: 5, season: 5 };
}

describe('buildTopSongsItems', () => {
  it('returns empty array if no ranked scores', () => {
    const scores = [makeScore('s1', 0, 0)];
    const songMap = new Map([['s1', makeSong('s1')]]);
    const items = buildTopSongsItems(t, inst, scores, songMap, 'Player', navigateToSongDetail);
    expect(items.length).toBe(0);
  });

  it('returns top 5 header + songs for <= 5 ranked scores', () => {
    const scores = Array.from({ length: 3 }, (_, i) => makeScore(`s${i}`, i + 1, 100));
    const songMap = new Map(scores.map(s => [s.songId, makeSong(s.songId)]));
    const items = buildTopSongsItems(t, inst, scores, songMap, 'Player', navigateToSongDetail);
    // 1 header + 1 song list = 2 items
    expect(items.length).toBe(2);
    expect(items[0]!.key).toContain('top-hdr');
  });

  it('returns top + bottom sections for > 5 ranked scores', () => {
    const scores = Array.from({ length: 12 }, (_, i) => makeScore(`s${i}`, i + 1, 100));
    const songMap = new Map(scores.map(s => [s.songId, makeSong(s.songId)]));
    const items = buildTopSongsItems(t, inst, scores, songMap, 'Player', navigateToSongDetail);
    // 1 top header + 1 top list + 1 bottom header + 1 bottom list = 4 items
    expect(items.length).toBe(4);
    expect(items[2]!.key).toContain('bot-hdr');
  });

  it('renders song rows with correct titles', () => {
    const scores = [makeScore('s1', 1, 100)];
    const songMap = new Map([['s1', makeSong('s1')]]);
    const items = buildTopSongsItems(t, inst, scores, songMap, 'Player', navigateToSongDetail);
    const songListItem = items.find(i => i.key.includes('top-songs'));
    const { container } = render(<>{songListItem!.node}</>);
    expect(container.textContent).toContain('Song s1');
  });

  it('uses songId prefix when song not in map', () => {
    const scores = [makeScore('unknown-song-id-1234', 1, 100)];
    const songMap = new Map<string, Song>();
    const items = buildTopSongsItems(t, inst, scores, songMap, 'Player', navigateToSongDetail);
    const songListItem = items.find(i => i.key.includes('top-songs'));
    const { container } = render(<>{songListItem!.node}</>);
    expect(container.textContent).toContain('unknown-');
  });
});
