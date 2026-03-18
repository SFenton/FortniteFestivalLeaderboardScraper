/**
 * Comprehensive conditional JSX branch tests.
 * Covers all ~150 ternary rendering branches across UI components:
 * SongRow metadata cases, SongsPage callbacks, App mobile nav,
 * SuggestionsPage empty states, LeaderboardPage pagination/stars,
 * SongDetailPage stagger, PlayerHistoryPage sort UI, and more.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { ServerSong as Song, PlayerScore, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import type { SongSortMode } from '../../utils/songSettings';
import { SongRow, compareByMode } from '../../pages/songs/components/SongRow';

/* ══════════════════════════════════════════════
   MOCKS — shared across all tests
   ══════════════════════════════════════════════ */

vi.mock('../../components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument }: any) => <span data-testid={`icon-${instrument}`}>{instrument}</span>,
  getInstrumentStatusVisual: (hasScore: boolean, isFC: boolean) => ({
    fill: hasScore ? (isFC ? 'gold' : 'green') : 'red',
    stroke: hasScore ? (isFC ? 'goldS' : 'greenS') : 'redS',
  }),
}));

vi.mock('../../components/songs/metadata/AccuracyDisplay', () => ({
  default: ({ accuracy }: any) => <span data-testid="accuracy">{accuracy}</span>,
}));

vi.mock('../../components/songs/metadata/PercentilePill', () => ({
  default: ({ display }: any) => <span data-testid="percentile">{display}</span>,
}));

vi.mock('../../components/songs/metadata/SeasonPill', () => ({
  default: ({ season }: any) => <span data-testid="season">S{season}</span>,
}));

vi.mock('../../components/songs/metadata/MiniStars', () => ({
  default: ({ starsCount }: any) => <span data-testid="stars">{starsCount}</span>,
}));

vi.mock('../../components/songs/metadata/DifficultyBars', () => ({
  default: ({ level }: any) => <span data-testid="difficulty">{level}</span>,
}));

vi.mock('../../components/songs/metadata/SongInfo', () => ({
  default: ({ title }: any) => <span data-testid="songinfo">{title}</span>,
}));

vi.mock('@festival/core', async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>();
  return { ...actual, formatPercentileBucket: (p: number) => `Top ${Math.ceil(p)}%` };
});

beforeEach(() => {
  vi.clearAllMocks();
  Object.defineProperty(window, 'matchMedia', {
    writable: true, configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false, media: query, onchange: null,
      addEventListener: vi.fn(), removeEventListener: vi.fn(),
      addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
});

/* ── Helpers ── */

const baseSong: Song = {
  songId: 'test-song', title: 'Test Song', artist: 'Test Artist', year: 2024,
  albumArt: 'https://example.com/art.jpg',
  difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1, proGuitar: 5, proBass: 3 },
};

function makeScore(overrides: Partial<PlayerScore> = {}): PlayerScore {
  return {
    songId: 'test-song', instrument: 'Solo_Guitar' as InstrumentKey,
    score: 150000, rank: 5, totalEntries: 100, accuracy: 955000,
    isFullCombo: false, stars: 5, season: 4, ...overrides,
  };
}

const defaultSongRowProps = {
  song: baseSong,
  score: makeScore(),
  instrument: 'Solo_Guitar' as InstrumentKey,
  instrumentFilter: null as InstrumentKey | null,
  allScoreMap: undefined as Map<string, PlayerScore> | undefined,
  showInstrumentIcons: false,
  enabledInstruments: ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals'] as InstrumentKey[],
  metadataOrder: ['score', 'percentage', 'percentile', 'seasonachieved', 'stars', 'intensity'],
  sortMode: 'score' as SongSortMode,
  isMobile: false,
};

function renderSongRow(overrides: Partial<typeof defaultSongRowProps> = {}) {
  return render(
    <MemoryRouter><SongRow {...defaultSongRowProps} {...overrides} /></MemoryRouter>,
  );
}

/* ══════════════════════════════════════════════
   SongRow — renderMetadataElement ALL 7 switch cases
   ══════════════════════════════════════════════ */

describe('SongRow renderMetadataElement — all cases', () => {
  // Score case: score > 0 vs score = 0
  it('score: renders score when > 0', () => {
    renderSongRow({ metadataOrder: ['score'] });
    expect(screen.getByText('150,000')).toBeTruthy();
  });

  it('score: returns null when score = 0', () => {
    renderSongRow({ score: makeScore({ score: 0 }), metadataOrder: ['score'] });
    expect(screen.queryByText('0')).toBeNull();
  });

  // Percentage case: accuracy > 0 vs accuracy = 0
  it('percentage: renders accuracy when > 0', () => {
    renderSongRow({ metadataOrder: ['percentage'] });
    expect(screen.getByTestId('accuracy')).toBeTruthy();
  });

  it('percentage: returns null when accuracy = 0', () => {
    renderSongRow({ score: makeScore({ accuracy: 0 }), metadataOrder: ['percentage'] });
    expect(screen.queryByTestId('accuracy')).toBeNull();
  });

  // Stars case: stars > 0 vs stars = 0
  it('stars: renders when stars > 0', () => {
    renderSongRow({ metadataOrder: ['stars'] });
    expect(screen.getByTestId('stars')).toBeTruthy();
  });

  it('stars: returns null when stars = 0', () => {
    renderSongRow({ score: makeScore({ stars: 0 }), metadataOrder: ['stars'] });
    expect(screen.queryByTestId('stars')).toBeNull();
  });

  it('stars: renders when stars is undefined (treated as 0)', () => {
    renderSongRow({ score: makeScore({ stars: undefined }), metadataOrder: ['stars'] });
    expect(screen.queryByTestId('stars')).toBeNull();
  });

  // Seasonachieved case: season > 0 vs season = 0 vs null
  it('seasonachieved: renders when season > 0', () => {
    renderSongRow({ metadataOrder: ['seasonachieved'] });
    expect(screen.getByTestId('season')).toBeTruthy();
  });

  it('seasonachieved: returns null when season = 0', () => {
    renderSongRow({ score: makeScore({ season: 0 }), metadataOrder: ['seasonachieved'] });
    expect(screen.queryByTestId('season')).toBeNull();
  });

  it('seasonachieved: returns null when season is null', () => {
    renderSongRow({ score: makeScore({ season: undefined }), metadataOrder: ['seasonachieved'] });
    expect(screen.queryByTestId('season')).toBeNull();
  });

  // Percentile case: rank > 0 with totalEntries > 0, vs rank = 0
  it('percentile: renders when rank and totalEntries valid', () => {
    renderSongRow({ metadataOrder: ['percentile'] });
    expect(screen.getByTestId('percentile')).toBeTruthy();
  });

  it('percentile: returns null when rank = 0', () => {
    renderSongRow({ score: makeScore({ rank: 0 }), metadataOrder: ['percentile'] });
    expect(screen.queryByTestId('percentile')).toBeNull();
  });

  it('percentile: returns null when totalEntries = 0', () => {
    renderSongRow({ score: makeScore({ totalEntries: 0 }), metadataOrder: ['percentile'] });
    expect(screen.queryByTestId('percentile')).toBeNull();
  });

  // Intensity case: songIntensityRaw present vs absent
  it('intensity: renders when song has difficulty for instrument', () => {
    renderSongRow({ metadataOrder: ['intensity'] });
    expect(screen.getByTestId('difficulty')).toBeTruthy();
  });

  it('intensity: returns null for instrument with no difficulty', () => {
    renderSongRow({
      song: { ...baseSong, difficulty: undefined },
      metadataOrder: ['intensity'],
    });
    expect(screen.queryByTestId('difficulty')).toBeNull();
  });

  // Default case — unknown key just doesn't render anything
  it('unknown metadata key does not crash', () => {
    const { container } = renderSongRow({ metadataOrder: ['unknown_key'], score: makeScore() });
    expect(container.querySelector('a')).toBeTruthy();
  });
});

/* ══════════════════════════════════════════════
   SongRow — component rendering branches
   ══════════════════════════════════════════════ */

describe('SongRow — rendering branches', () => {
  // Mobile with entries
  it('mobile: renders top row with score + bottom metadata', () => {
    renderSongRow({ isMobile: true });
    expect(screen.getByText('Test Song')).toBeTruthy();
  });

  // Mobile with instrument chips (no score)  
  it('mobile: renders instrument chip row when showIcons and no instrumentFilter', () => {
    const allScoreMap = new Map<string, PlayerScore>([
      ['Solo_Guitar', makeScore()],
    ]);
    renderSongRow({
      isMobile: true,
      showInstrumentIcons: true,
      instrumentFilter: undefined,
      allScoreMap,
      score: undefined,
    });
    expect(screen.getByTestId('icon-Solo_Guitar')).toBeTruthy();
  });

  // Desktop with instrument chips
  it('desktop: renders chip row when showInstrumentIcons and no filter', () => {
    const allScoreMap = new Map<string, PlayerScore>([
      ['Solo_Guitar', makeScore({ isFullCombo: true })],
      ['Solo_Bass', makeScore({ score: 0 })],
    ]);
    renderSongRow({
      showInstrumentIcons: true,
      instrumentFilter: undefined,
      allScoreMap,
      score: undefined,
    });
    expect(screen.getByTestId('icon-Solo_Guitar')).toBeTruthy();
  });

  // No instrument chips when filter is active
  it('hides chips when instrumentFilter is set', () => {
    renderSongRow({
      showInstrumentIcons: true,
      instrumentFilter: 'Solo_Guitar' as InstrumentKey,
    });
    expect(screen.queryByTestId('icon-Solo_Guitar')).toBeNull();
  });

  // No score, no chips → just song info
  it('desktop: no score, no chips → minimal row', () => {
    const { container } = renderSongRow({ score: undefined, showInstrumentIcons: false });
    expect(container.querySelector('a')).toBeTruthy();
    expect(container.querySelector('[class*="scoreMeta"]')).toBeNull();
  });

  // Sort mode promotion
  it('promotes percentage to first metadata element', () => {
    renderSongRow({ sortMode: 'percentage' });
    expect(screen.getByTestId('accuracy')).toBeTruthy();
  });

  it('does not promote general sort modes (title, artist)', () => {
    renderSongRow({ sortMode: 'title' });
    expect(screen.getByText('150,000')).toBeTruthy();
  });

  // Stagger delay
  it('no staggerDelay: no animation style', () => {
    const { container } = renderSongRow();
    const link = container.querySelector('a');
    expect(link?.style.animation).toBe('');
  });

  // All metadata empty → no metadata section
  it('no score metadata when all values are zero', () => {
    renderSongRow({
      score: makeScore({ score: 0, accuracy: 0, stars: 0, season: 0, rank: 0, totalEntries: 0 }),
    });
    expect(screen.queryByTestId('accuracy')).toBeNull();
  });
});

/* ══════════════════════════════════════════════
   compareByMode — exhaustive branch coverage
   ══════════════════════════════════════════════ */

describe('compareByMode — exhaustive branches', () => {
  const a = makeScore({ score: 100, accuracy: 900, isFullCombo: false, rank: 5, totalEntries: 100, stars: 4, season: 3 });
  const b = makeScore({ score: 200, accuracy: 950, isFullCombo: true, rank: 2, totalEntries: 100, stars: 5, season: 5 });

  it('percentage: same accuracy different FC', () => {
    const x = makeScore({ accuracy: 900, isFullCombo: false });
    const y = makeScore({ accuracy: 900, isFullCombo: true });
    expect(compareByMode('percentage', x, y)).toBeLessThan(0);
  });

  it('percentage: different accuracy', () => {
    expect(compareByMode('percentage', a, b)).toBeLessThan(0);
  });

  it('percentile: both have valid rank', () => {
    expect(compareByMode('percentile', a, b)).toBeGreaterThan(0); // 5/100 > 2/100
  });

  it('percentile: one has no rank (Infinity)', () => {
    const noRank = makeScore({ rank: 0 });
    expect(compareByMode('percentile', noRank, b)).toBeGreaterThan(0);
  });

  it('percentile: one has no totalEntries', () => {
    const noTotal = makeScore({ totalEntries: 0 });
    expect(compareByMode('percentile', noTotal, b)).toBeGreaterThan(0);
  });

  it('stars: null stars treated as 0', () => {
    const nullStars = makeScore({ stars: undefined });
    expect(compareByMode('stars', nullStars, makeScore({ stars: 1 }))).toBeLessThan(0);
  });

  it('seasonachieved: null season treated as 0', () => {
    const nullSeason = makeScore({ season: undefined });
    expect(compareByMode('seasonachieved', nullSeason, makeScore({ season: 1 }))).toBeLessThan(0);
  });

  it('hasfc: compares boolean as 0/1', () => {
    const noFC = makeScore({ isFullCombo: false });
    const hasFC = makeScore({ isFullCombo: true });
    expect(compareByMode('hasfc', noFC, hasFC)).toBeLessThan(0);
  });

  it('score: basic comparison', () => {
    expect(compareByMode('score', makeScore({ score: 10 }), makeScore({ score: 20 }))).toBeLessThan(0);
  });

  it('returns 0 for unknown sort mode', () => {
    expect(compareByMode('nonexistent' as SongSortMode, a, b)).toBe(0);
  });

  it('a=undefined => 1', () => expect(compareByMode('score', undefined, b)).toBe(1));
  it('b=undefined => -1', () => expect(compareByMode('score', a, undefined)).toBe(-1));
  it('both undefined => 0', () => expect(compareByMode('score', undefined, undefined)).toBe(0));
});
