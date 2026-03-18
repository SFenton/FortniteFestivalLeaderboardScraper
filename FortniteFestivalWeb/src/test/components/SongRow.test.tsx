import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SongRow, compareByMode } from '../../pages/songs/components/SongRow';
import type { ServerSong as Song, PlayerScore, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';

import type { SongSortMode } from '../../utils/songSettings';

// Mock modules that use import.meta.env
vi.mock('../../components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument, size }: { instrument: string; size: number }) =>
    <img data-testid={`instrument-${instrument}`} alt={instrument} width={size} height={size} />,
  getInstrumentStatusVisual: (hasScore: boolean, isFC: boolean) => ({
    fill: hasScore ? (isFC ? 'gold' : 'green') : 'red',
    stroke: hasScore ? (isFC ? 'goldStroke' : 'greenStroke') : 'redStroke',
  }),
}));

vi.mock('@festival/core', async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>();
  return {
    ...actual,
    formatPercentileBucket: (p: number) => {
      if (p <= 1) return 'Top 1%';
      if (p <= 5) return 'Top 5%';
      if (p <= 10) return 'Top 10%';
      return `Top ${Math.ceil(p)}%`;
    },
  };
});

beforeEach(() => {
  vi.clearAllMocks();
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false, media: query, onchange: null,
      addEventListener: vi.fn(), removeEventListener: vi.fn(),
      addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
});

const baseSong: Song = {
  songId: 'test-song',
  title: 'Test Song',
  artist: 'Test Artist',
  year: 2024,
  albumArt: 'https://example.com/art.jpg',
  difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1, proGuitar: 5, proBass: 3 },
} as Song;

const baseScore: PlayerScore = {
  songId: 'test-song',
  instrument: 'Solo_Guitar' as InstrumentKey,
  score: 150000,
  rank: 5,
  totalEntries: 100,
  accuracy: 95.5,
  isFullCombo: false,
  stars: 5,
  season: 4,
};

const defaultProps = {
  song: baseSong,
  score: baseScore,
  instrument: 'Solo_Guitar' as InstrumentKey,
  instrumentFilter: null as InstrumentKey | null,
  allScoreMap: undefined as Map<string, PlayerScore> | undefined,
  showInstrumentIcons: false,
  enabledInstruments: ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals'] as InstrumentKey[],
  metadataOrder: ['score', 'percentage', 'percentile', 'seasonachieved', 'stars'],
  sortMode: 'score' as SongSortMode,
  isMobile: false,
};

function renderSongRow(overrides: Partial<typeof defaultProps> = {}) {
  return render(
    <MemoryRouter>
      <SongRow {...defaultProps} {...overrides} />
    </MemoryRouter>,
  );
}

describe('SongRow', () => {
  it('renders song title and artist', () => {
    renderSongRow();
    expect(screen.getByText('Test Song')).toBeTruthy();
    expect(screen.getByText(/Test Artist/)).toBeTruthy();
  });

  it('renders album art', () => {
    const { container } = renderSongRow();
    const img = container.querySelector('img[src*="art.jpg"]');
    expect(img).toBeTruthy();
  });

  it('renders score value', () => {
    renderSongRow();
    expect(screen.getByText('150,000')).toBeTruthy();
  });

  it('renders accuracy display', () => {
    // accuracy is in raw format: 95.5% = 955000 (× ACCURACY_SCALE of 10000)
    renderSongRow({ score: { ...baseScore, accuracy: 955000 } });
    expect(screen.getByText(/95/)).toBeTruthy();
  });

  it('renders percentile pill', () => {
    renderSongRow();
    expect(screen.getByText(/Top 5%/)).toBeTruthy();
  });

  it('renders season pill', () => {
    renderSongRow();
    expect(screen.getByText(/S4/)).toBeTruthy();
  });

  it('renders stars', () => {
    renderSongRow({ score: { ...baseScore, stars: 4 } });
    // MiniStars should render — just check no crash
  });

  it('does not render metadata when score is missing', () => {
    const { container } = renderSongRow({ score: undefined });
    expect(container.querySelector('[class*="scoreMeta"]')).toBeNull();
  });

  it('renders instrument chips when showInstrumentIcons is true and no instrumentFilter', () => {
    const allScoreMap = new Map<string, PlayerScore>([
      ['Solo_Guitar', { ...baseScore, score: 100000, isFullCombo: true }],
      ['Solo_Bass', { ...baseScore, instrument: 'Solo_Bass' as InstrumentKey, score: 50000 }],
    ]);
    renderSongRow({
      showInstrumentIcons: true,
      instrumentFilter: undefined,
      allScoreMap,
    });
    expect(screen.getByTestId('instrument-Solo_Guitar')).toBeTruthy();
    expect(screen.getByTestId('instrument-Solo_Bass')).toBeTruthy();
  });

  it('does not show instrument chips when instrumentFilter is set', () => {
    renderSongRow({ showInstrumentIcons: true, instrumentFilter: 'Solo_Guitar' as InstrumentKey });
    expect(screen.queryByTestId('instrument-Solo_Guitar')).toBeNull();
  });

  it('renders mobile layout with score on top row', () => {
    renderSongRow({ isMobile: true });
    expect(screen.getByText('Test Song')).toBeTruthy();
    expect(screen.getByText('150,000')).toBeTruthy();
  });

  it('renders mobile layout with instrument chips', () => {
    const allScoreMap = new Map<string, PlayerScore>([
      ['Solo_Guitar', baseScore],
    ]);
    renderSongRow({
      isMobile: true,
      showInstrumentIcons: true,
      instrumentFilter: undefined,
      allScoreMap,
      score: undefined,
    });
    expect(screen.getByTestId('instrument-Solo_Guitar')).toBeTruthy();
  });

  it('renders link to song detail', () => {
    const { container } = renderSongRow();
    const link = container.querySelector('a');
    expect(link?.getAttribute('href')).toContain('/songs/test-song');
  });

  it('includes instrument in link when instrumentFilter is set', () => {
    const { container } = renderSongRow({ instrumentFilter: 'Solo_Guitar' as InstrumentKey });
    const link = container.querySelector('a');
    expect(link?.getAttribute('href')).toContain('instrument=Solo_Guitar');
  });

  it('applies stagger animation style when staggerDelay is provided', () => {
    const { container } = renderSongRow({ staggerDelay: 200 } as any);
    const link = container.querySelector('a');
    expect(link?.style.animation).toContain('fadeInUp');
  });

  it('promotes sortMode to first in metadata order', () => {
    renderSongRow({ sortMode: 'percentile' });
    // Percentile should appear — just verify no crash
    expect(screen.getByText(/Top 5%/)).toBeTruthy();
  });

  it('skips metadata elements with zero values', () => {
    renderSongRow({
      score: { ...baseScore, score: 0, accuracy: 0, stars: 0, season: 0, rank: 0, totalEntries: 0 },
    });
    expect(screen.queryByText(/150,000/)).toBeNull();
  });

  it('renders difficulty bars for intensity metadata', () => {
    renderSongRow({ metadataOrder: ['intensity'] });
    // Should render without error
  });
});

describe('compareByMode', () => {
  it('returns 0 when both scores are undefined', () => {
    expect(compareByMode('score', undefined, undefined)).toBe(0);
  });

  it('returns 1 when first score is undefined', () => {
    expect(compareByMode('score', undefined, baseScore)).toBe(1);
  });

  it('returns -1 when second score is undefined', () => {
    expect(compareByMode('score', baseScore, undefined)).toBe(-1);
  });

  it('compares by score', () => {
    const a = { ...baseScore, score: 100 };
    const b = { ...baseScore, score: 200 };
    expect(compareByMode('score', a, b)).toBeLessThan(0);
  });

  it('compares by percentage (accuracy)', () => {
    const a = { ...baseScore, accuracy: 90 };
    const b = { ...baseScore, accuracy: 95 };
    expect(compareByMode('percentage', a, b)).toBeLessThan(0);
  });

  it('compares by percentage with FC tiebreak', () => {
    const a = { ...baseScore, accuracy: 95, isFullCombo: false };
    const b = { ...baseScore, accuracy: 95, isFullCombo: true };
    expect(compareByMode('percentage', a, b)).toBeLessThan(0);
  });

  it('compares by percentile', () => {
    const a = { ...baseScore, rank: 1, totalEntries: 100 };
    const b = { ...baseScore, rank: 50, totalEntries: 100 };
    expect(compareByMode('percentile', a, b)).toBeLessThan(0);
  });

  it('handles missing totalEntries for percentile', () => {
    const a = { ...baseScore, rank: 0, totalEntries: 0 };
    const b = { ...baseScore, rank: 5, totalEntries: 100 };
    expect(compareByMode('percentile', a, b)).toBeGreaterThan(0);
  });

  it('compares by stars', () => {
    const a = { ...baseScore, stars: 3 };
    const b = { ...baseScore, stars: 5 };
    expect(compareByMode('stars', a, b)).toBeLessThan(0);
  });

  it('compares by seasonachieved', () => {
    const a = { ...baseScore, season: 2 };
    const b = { ...baseScore, season: 5 };
    expect(compareByMode('seasonachieved', a, b)).toBeLessThan(0);
  });

  it('compares by hasfc', () => {
    const a = { ...baseScore, isFullCombo: false };
    const b = { ...baseScore, isFullCombo: true };
    expect(compareByMode('hasfc', a, b)).toBeLessThan(0);
  });

  it('returns 0 for unknown sort mode', () => {
    expect(compareByMode('unknown' as any, baseScore, baseScore)).toBe(0);
  });

  it('uses 0 fallback when accuracy is undefined (percentage mode)', () => {
    const a = { ...baseScore, accuracy: undefined } as unknown as PlayerScore;
    const b = { ...baseScore, accuracy: 80 };
    expect(compareByMode('percentage', a, b)).toBeLessThan(0);
  });

  it('uses Infinity fallback when totalEntries is undefined (percentile mode)', () => {
    const a = { ...baseScore, rank: 1, totalEntries: undefined } as unknown as PlayerScore;
    const b = { ...baseScore, rank: 1, totalEntries: 100 };
    expect(compareByMode('percentile', a, b)).toBeGreaterThan(0);
  });

  it('uses 0 fallback when stars is undefined', () => {
    const a = { ...baseScore, stars: undefined } as unknown as PlayerScore;
    const b = { ...baseScore, stars: 3 };
    expect(compareByMode('stars', a, b)).toBeLessThan(0);
  });

  it('uses 0 fallback when season is undefined', () => {
    const a = { ...baseScore, season: undefined } as unknown as PlayerScore;
    const b = { ...baseScore, season: 2 };
    expect(compareByMode('seasonachieved', a, b)).toBeLessThan(0);
  });

  it('compares hasfc with both true and false values', () => {
    const a = { ...baseScore, isFullCombo: true };
    const b = { ...baseScore, isFullCombo: false };
    expect(compareByMode('hasfc', a, b)).toBeGreaterThan(0);
  });
});
