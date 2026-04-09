import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SongRow, compareByMode } from '../../../../src/pages/songs/components/SongRow';
import { resolvePillFitsTopRow } from '../../../../src/pages/songs/layoutMode';
import type { ServerSong as Song, PlayerScore, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';

import type { SongSortMode } from '../../../../src/utils/songSettings';

// Mock modules that use import.meta.env
vi.mock('../../../../src/components/display/InstrumentIcons', () => ({
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

vi.mock('../../../../src/components/songs/metadata/AccuracyDisplay', () => ({
  default: ({ accuracy }: any) => <span data-testid="accuracy">{accuracy}</span>,
}));

vi.mock('../../../../src/components/songs/metadata/PercentilePill', () => ({
  default: ({ display }: any) => <span data-testid="percentile">{display}</span>,
}));

vi.mock('../../../../src/components/songs/metadata/SeasonPill', () => ({
  default: ({ season }: any) => <span data-testid="season">S{season}</span>,
}));

vi.mock('../../../../src/components/songs/metadata/MiniStars', () => ({
  default: ({ starsCount }: any) => <span data-testid="mini-stars">{starsCount}</span>,
}));

vi.mock('../../../../src/components/songs/metadata/DifficultyBars', () => ({
  default: ({ level }: any) => <span data-testid="difficulty">{level}</span>,
}));

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

function ps(o: Partial<PlayerScore> = {}): PlayerScore {
  return { ...baseScore, ...o };
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

  it('wraps songInfo in mobileTopRow on mobile with no score and no chips', () => {
    const { container } = renderSongRow({ isMobile: true, score: undefined, showInstrumentIcons: false });
    expect(screen.getByText('Test Song')).toBeTruthy();
    expect(screen.getByText(/Test Artist/)).toBeTruthy();
    // SongInfo children should be inside the mobileTopRow wrapper (flexRow), not direct children of the Link
    const link = container.querySelector('a')!;
    // The Link's only child should be the mobileTopRow div, not bare AlbumArt/text fragments
    expect(link.children).toHaveLength(1);
    const topRow = link.children[0] as HTMLElement;
    expect(topRow.tagName).toBe('DIV');
    // Album art + text div inside the wrapper
    expect(topRow.children.length).toBeGreaterThanOrEqual(2);
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

  it('keeps the score in the wrapped metadata row until the upper threshold is cleared', () => {
    const { container, rerender } = render(
      <MemoryRouter>
        <SongRow {...defaultProps} isMobile containerWidth={280} />
      </MemoryRouter>,
    );

    expect(resolvePillFitsTopRow(280, true)).toBe(false);
    expect(container.querySelector('[data-metadata-key="score"]')).toBeTruthy();

    rerender(
      <MemoryRouter>
        <SongRow {...defaultProps} isMobile containerWidth={318} />
      </MemoryRouter>,
    );

    expect(resolvePillFitsTopRow(318, false)).toBe(false);
    expect(container.querySelector('[data-metadata-key="score"]')).toBeTruthy();

    rerender(
      <MemoryRouter>
        <SongRow {...defaultProps} isMobile containerWidth={326} />
      </MemoryRouter>,
    );

    expect(resolvePillFitsTopRow(326, false)).toBe(true);
    expect(container.querySelector('[data-metadata-key="score"]')).toBeNull();
  });

  it('keeps the score in the top row until the lower threshold is crossed', () => {
    const { container, rerender } = render(
      <MemoryRouter>
        <SongRow {...defaultProps} isMobile containerWidth={320} />
      </MemoryRouter>,
    );

    expect(resolvePillFitsTopRow(320, true)).toBe(true);
    expect(container.querySelector('[data-metadata-key="score"]')).toBeNull();

    rerender(
      <MemoryRouter>
        <SongRow {...defaultProps} isMobile containerWidth={301} />
      </MemoryRouter>,
    );

    expect(resolvePillFitsTopRow(301, true)).toBe(true);
    expect(container.querySelector('[data-metadata-key="score"]')).toBeNull();

    rerender(
      <MemoryRouter>
        <SongRow {...defaultProps} isMobile containerWidth={298} />
      </MemoryRouter>,
    );

    expect(resolvePillFitsTopRow(298, true)).toBe(false);
    expect(container.querySelector('[data-metadata-key="score"]')).toBeTruthy();
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

describe('SongRow false-path branches', () => {
  it('score=0 → no score rendered', () => {
    renderSongRow({ score: ps({ score: 0 }), metadataOrder: ['score'] });
    expect(screen.queryByText('0')).toBeNull();
  });

  it('accuracy=0 → renders accuracy pill with 0', () => {
    renderSongRow({ score: ps({ accuracy: 0 }), metadataOrder: ['percentage'] });
    expect(screen.queryByTestId('accuracy')).toBeTruthy();
    expect(screen.getByTestId('accuracy').textContent).toBe('0');
  });

  it('stars=0 → no stars rendered', () => {
    renderSongRow({ score: ps({ stars: 0 }), metadataOrder: ['stars'] });
    expect(screen.queryByTestId('mini-stars')).toBeNull();
  });

  it('season=null → no season rendered', () => {
    renderSongRow({ score: ps({ season: undefined }), metadataOrder: ['seasonachieved'] });
    expect(screen.queryByTestId('season')).toBeNull();
  });

  it('season=0 → no season rendered', () => {
    renderSongRow({ score: ps({ season: 0 }), metadataOrder: ['seasonachieved'] });
    expect(screen.queryByTestId('season')).toBeNull();
  });

  it('rank=0 → no percentile rendered', () => {
    renderSongRow({ score: ps({ rank: 0 }), metadataOrder: ['percentile'] });
    expect(screen.queryByTestId('percentile')).toBeNull();
  });

  it('totalEntries=0 → no percentile rendered', () => {
    renderSongRow({ score: ps({ totalEntries: 0 }), metadataOrder: ['percentile'] });
    expect(screen.queryByTestId('percentile')).toBeNull();
  });

  it('no difficulty → no intensity rendered', () => {
    renderSongRow({ song: { ...baseSong, difficulty: undefined }, metadataOrder: ['intensity'] });
    expect(screen.queryByTestId('difficulty')).toBeNull();
  });

  it('unknown metadata key → nothing crashes', () => {
    renderSongRow({ metadataOrder: ['xyz'] });
    expect(screen.getByText('Test Song')).toBeTruthy();
  });
});

describe('SongRow ?? fallback branches', () => {
  it('accuracy=undefined → renders accuracy pill', () => {
    renderSongRow({ score: ps({ accuracy: undefined }), metadataOrder: ['percentage'] });
    expect(screen.queryByTestId('accuracy')).toBeTruthy();
  });

  it('stars=undefined triggers ?? 0 fallback', () => {
    renderSongRow({ score: ps({ stars: undefined }), metadataOrder: ['stars'] });
    expect(screen.queryByTestId('mini-stars')).toBeNull();
  });

  it('totalEntries=undefined triggers ?? 0 in percentile', () => {
    renderSongRow({ score: ps({ totalEntries: undefined }), metadataOrder: ['percentile'] });
    expect(screen.queryByTestId('percentile')).toBeNull();
  });

  it('intensity with undefined difficulty key', () => {
    renderSongRow({ instrument: 'Solo_PeripheralGuitar' as any, metadataOrder: ['intensity'] });
    expect(screen.getByText('Test Song')).toBeTruthy();
  });
});

describe('SongRow — rendering branches', () => {
  it('promotes percentage to first metadata element', () => {
    renderSongRow({ sortMode: 'percentage' as SongSortMode });
    expect(screen.getByTestId('accuracy')).toBeTruthy();
  });

  it('does not promote general sort modes (title, artist)', () => {
    renderSongRow({ sortMode: 'title' as SongSortMode });
    expect(screen.getByText('150,000')).toBeTruthy();
  });

  it('no staggerDelay: no animation style', () => {
    const { container } = renderSongRow();
    const link = container.querySelector('a');
    expect(link?.style.animation).toBe('');
  });
});

describe('SongRow — diffKey null branch', () => {
  it('renders without instrumentFilter (diffKey is null)', () => {
    const { container } = renderSongRow({
      score: undefined,
      instrument: undefined as any,
      instrumentFilter: null,
      allScoreMap: new Map(),
      showInstrumentIcons: true,
      metadataOrder: [],
      sortMode: 'title' as SongSortMode,
    });
    expect(container.querySelector('a')).toBeTruthy();
  });
});

describe('compareByMode — b-side ?? fallback branches', () => {
  it('b.accuracy undefined in percentage mode', () => {
    const a = { ...baseScore, accuracy: 80 };
    const b = { ...baseScore, accuracy: undefined } as unknown as PlayerScore;
    expect(compareByMode('percentage', a, b)).toBeGreaterThan(0);
  });

  it('percentage mode: a.isFullCombo true, b.isFullCombo false', () => {
    const a = { ...baseScore, accuracy: 95, isFullCombo: true };
    const b = { ...baseScore, accuracy: 95, isFullCombo: false };
    expect(compareByMode('percentage', a, b)).toBeGreaterThan(0);
  });

  it('b.totalEntries undefined in percentile mode', () => {
    const a = { ...baseScore, rank: 5, totalEntries: 100 };
    const b = { ...baseScore, rank: 1, totalEntries: undefined } as unknown as PlayerScore;
    expect(compareByMode('percentile', a, b)).toBeLessThan(0);
  });

  it('b.stars undefined in stars mode', () => {
    const a = { ...baseScore, stars: 3 };
    const b = { ...baseScore, stars: undefined } as unknown as PlayerScore;
    expect(compareByMode('stars', a, b)).toBeGreaterThan(0);
  });

  it('b.season undefined in seasonachieved mode', () => {
    const a = { ...baseScore, season: 2 };
    const b = { ...baseScore, season: undefined } as unknown as PlayerScore;
    expect(compareByMode('seasonachieved', a, b)).toBeGreaterThan(0);
  });
});

describe('SongRow — mobile with instrumentFilter', () => {
  it('renders mobile layout with instrumentFilter and entries', () => {
    const { container } = renderSongRow({ isMobile: true, instrumentFilter: 'Solo_Guitar' as InstrumentKey });
    const link = container.querySelector('a');
    expect(link?.getAttribute('href')).toContain('instrument=Solo_Guitar');
  });
});

describe('SongRow — maxdistance sort mode', () => {
  const songWithMax: Song = {
    ...baseSong,
    maxScores: { Solo_Guitar: 200000 } as Partial<Record<InstrumentKey, number>>,
  };

  it('shows dual score format (score / maxScore) when maxScore is available', () => {
    renderSongRow({
      song: songWithMax,
      score: ps({ score: 150000 }),
      sortMode: 'maxdistance' as SongSortMode,
      metadataOrder: ['score', 'maxdistance'],
    });
    expect(screen.getByText('150,000')).toBeTruthy();
    expect(screen.getByText('200,000')).toBeTruthy();
    expect(screen.getByText('/')).toBeTruthy();
  });

  it('shows percentage pill when maxScore is available', () => {
    renderSongRow({
      song: songWithMax,
      score: ps({ score: 150000 }),
      sortMode: 'maxdistance' as SongSortMode,
      metadataOrder: ['maxdistance'],
    });
    // 150000 / 200000 = 75.0%
    expect(screen.getByTestId('percentile')).toBeTruthy();
    expect(screen.getByText('75.0%')).toBeTruthy();
  });

  it('shows score / — fallback when maxScore is unavailable', () => {
    renderSongRow({
      song: baseSong, // no maxScores
      score: ps({ score: 150000 }),
      sortMode: 'maxdistance' as SongSortMode,
      metadataOrder: ['score', 'maxdistance'],
    });
    expect(screen.getByText('150,000')).toBeTruthy();
    expect(screen.getByText('/')).toBeTruthy();
    // "—" em-dash fallback for missing maxScore
    const dashes = screen.getAllByText('—');
    expect(dashes.length).toBeGreaterThanOrEqual(1);
  });

  it('shows — pill fallback for maxdistance metadata when maxScore is unavailable', () => {
    renderSongRow({
      song: baseSong, // no maxScores
      score: ps({ score: 150000 }),
      sortMode: 'maxdistance' as SongSortMode,
      metadataOrder: ['maxdistance'],
    });
    expect(screen.getByTestId('percentile')).toBeTruthy();
    expect(screen.getAllByText('—').length).toBeGreaterThan(0);
  });

  it('shows nothing for maxdistance metadata when score is 0', () => {
    renderSongRow({
      song: songWithMax,
      score: ps({ score: 0 }),
      sortMode: 'maxdistance' as SongSortMode,
      metadataOrder: ['maxdistance'],
    });
    expect(screen.queryByTestId('percentile')).toBeNull();
  });

  it('shows nothing for score when score is 0 in maxdistance mode', () => {
    renderSongRow({
      song: songWithMax,
      score: ps({ score: 0 }),
      sortMode: 'maxdistance' as SongSortMode,
      metadataOrder: ['score'],
    });
    // score=0 returns null in renderMetadataElement
    expect(screen.queryByText('200,000')).toBeNull();
  });
});

describe('SongRow — score alignment (sandwiched centering)', () => {
  function getScoreStyle(): CSSStyleDeclaration | undefined {
    return screen.getByText('150,000').style;
  }

  it('centers single-score when sandwiched between two visible metadata items', () => {
    renderSongRow({
      sortMode: 'title' as SongSortMode,
      metadataOrder: ['percentage', 'score', 'percentile'],
    });
    expect(getScoreStyle()?.textAlign).toBe('center');
  });

  it('right-aligns single-score when first in metadata order', () => {
    renderSongRow({
      sortMode: 'title' as SongSortMode,
      metadataOrder: ['score', 'percentage', 'percentile'],
    });
    expect(getScoreStyle()?.textAlign).toBe('right');
  });

  it('right-aligns single-score when last in metadata order', () => {
    renderSongRow({
      sortMode: 'title' as SongSortMode,
      metadataOrder: ['percentage', 'percentile', 'score'],
    });
    expect(getScoreStyle()?.textAlign).toBe('right');
  });

  it('right-aligns single-score when promoted by sort mode', () => {
    // Sorting by score promotes it to index 0 → not sandwiched
    renderSongRow({
      sortMode: 'score' as SongSortMode,
      metadataOrder: ['percentage', 'score', 'percentile'],
    });
    expect(getScoreStyle()?.textAlign).toBe('right');
  });

  it('right-aligns single-score when only visible metadata item', () => {
    renderSongRow({
      sortMode: 'title' as SongSortMode,
      metadataOrder: ['score'],
    });
    expect(getScoreStyle()?.textAlign).toBe('right');
  });

  it('does not center dual-score in maxdistance mode even when sandwiched', () => {
    const songWithMax: Song = {
      ...baseSong,
      maxScores: { Solo_Guitar: 200000 } as Partial<Record<InstrumentKey, number>>,
    };
    renderSongRow({
      song: songWithMax,
      score: ps({ score: 150000 }),
      sortMode: 'maxdistance' as SongSortMode,
      metadataOrder: ['score', 'maxdistance', 'percentage', 'percentile'],
    });
    // Primary score in dual format should stay right-aligned
    expect(getScoreStyle()?.textAlign).toBe('right');
  });
});
