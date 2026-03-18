import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { CategoryCard, SongRow } from '../../../../src/pages/suggestions/components/CategoryCard';
import type { SuggestionCategory, SuggestionSongItem } from '@festival/core/suggestions/types';
import type { LeaderboardData } from '@festival/core/models';

// Mock InstrumentIcons
vi.mock('../../../../src/components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument, size }: { instrument: string; size: number }) =>
    <img data-testid={`instrument-${instrument}`} alt={instrument} width={size} height={size} />,
  getInstrumentStatusVisual: (hasScore: boolean, isFC: boolean) => ({
    fill: hasScore ? (isFC ? 'gold' : 'green') : 'red',
    stroke: hasScore ? (isFC ? 'goldStroke' : 'greenStroke') : 'redStroke',
  }),
}));

// Mock useIsMobile
vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({
  useIsMobile: () => false,
  useIsMobileChrome: () => false,
}));

beforeEach(() => {
  vi.clearAllMocks();
});

function makeSong(id: string, overrides: Partial<SuggestionSongItem> = {}): SuggestionSongItem {
  return {
    songId: id,
    title: `Song ${id}`,
    artist: `Artist ${id}`,
    year: 2024,
    ...overrides,
  } as SuggestionSongItem;
}

function makeCategory(key: string, songs: SuggestionSongItem[], overrides: Partial<SuggestionCategory> = {}): SuggestionCategory {
  return {
    key,
    title: `Category ${key}`,
    description: `Description for ${key}`,
    songs,
    ...overrides,
  } as SuggestionCategory;
}

const albumArtMap = new Map<string, string>([
  ['s1', 'https://example.com/s1.jpg'],
  ['s2', 'https://example.com/s2.jpg'],
]);

const emptyScoresIndex: Record<string, LeaderboardData> = {};

function renderCategoryCard(category: SuggestionCategory, scores = emptyScoresIndex) {
  return render(
    <MemoryRouter>
      <CategoryCard category={category} albumArtMap={albumArtMap} scoresIndex={scores} />
    </MemoryRouter>,
  );
}

function renderSongRow(props: Partial<React.ComponentProps<typeof SongRow>> = {}) {
  const defaults = {
    song: makeSong('s1'),
    categoryKey: 'first_plays_mixed',
    albumArt: 'https://example.com/s1.jpg',
    leaderboardData: undefined,
  };
  return render(
    <MemoryRouter>
      <SongRow {...defaults} {...props} />
    </MemoryRouter>,
  );
}

describe('CategoryCard', () => {
  it('renders category title and description', () => {
    const cat = makeCategory('test_cat', [makeSong('s1')]);
    renderCategoryCard(cat);
    expect(screen.getByText('Category test_cat')).toBeTruthy();
    expect(screen.getByText('Description for test_cat')).toBeTruthy();
  });

  it('renders song rows for each song', () => {
    const cat = makeCategory('test_cat', [makeSong('s1'), makeSong('s2')]);
    renderCategoryCard(cat);
    expect(screen.getByText('Song s1')).toBeTruthy();
    expect(screen.getByText('Song s2')).toBeTruthy();
  });

  it('renders instrument icon for instrument-specific category', () => {
    const cat = makeCategory('unfc_guitar', [makeSong('s1', { instrumentKey: 'guitar' as any })]);
    renderCategoryCard(cat);
    expect(screen.getByTestId('instrument-guitar')).toBeTruthy();
  });

  it('does not render instrument icon for global category', () => {
    const cat = makeCategory('variety_pack', [makeSong('s1')]);
    renderCategoryCard(cat);
    // No instrument icon in header
    const header = screen.getByText('Category variety_pack').closest('[class*="cardHeader"]');
    expect(header?.querySelector('[data-testid^="instrument-"]')).toBeNull();
  });

  it('renders empty song list', () => {
    const cat = makeCategory('test_cat', []);
    const { container } = renderCategoryCard(cat);
    const songList = container.querySelector('[class*="songList"]');
    expect(songList?.children.length).toBe(0);
  });
});

describe('SongRow (CategoryCard)', () => {
  it('renders song title and artist', () => {
    renderSongRow();
    expect(screen.getByText('Song s1')).toBeTruthy();
    expect(screen.getByText(/Artist s1/)).toBeTruthy();
  });

  it('renders link to song detail page', () => {
    const { container } = renderSongRow({ song: makeSong('s1', { instrumentKey: 'guitar' as any }), categoryKey: 'near_fc' });
    const link = container.querySelector('a');
    expect(link?.getAttribute('href')).toContain('/songs/s1');
  });

  it('includes instrument query param for instrument-specific songs', () => {
    const { container } = renderSongRow({
      song: makeSong('s1', { instrumentKey: 'guitar' as any }),
      categoryKey: 'near_fc_guitar',
    });
    const link = container.querySelector('a');
    expect(link?.getAttribute('href')).toContain('instrument=Solo_Guitar');
  });

  it('renders hidden layout for variety_pack category (no right content)', () => {
    const { container } = renderSongRow({ categoryKey: 'variety_pack' });
    // hidden layout renders no right content
    expect(container.querySelector('[class*="badges"]')).toBeNull();
    expect(container.querySelector('[class*="instrumentChipsRow"]')).toBeNull();
  });

  it('renders hidden layout for unplayed_ category', () => {
    const { container } = renderSongRow({ categoryKey: 'unplayed_guitar' });
    expect(container.querySelector('[class*="badges"]')).toBeNull();
  });

  it('renders percentile layout for almost_elite category', () => {
    renderSongRow({
      song: makeSong('s1', { percentileDisplay: 'Top 5%', instrumentKey: 'guitar' as any }),
      categoryKey: 'almost_elite_guitar',
    });
    expect(screen.getByText('Top 5%')).toBeTruthy();
  });

  it('renders percentile layout for pct_push category', () => {
    renderSongRow({
      song: makeSong('s1', { percentileDisplay: 'Top 10%', instrumentKey: 'bass' as any }),
      categoryKey: 'pct_push_bass',
    });
    expect(screen.getByText('Top 10%')).toBeTruthy();
  });

  it('renders season layout for stale_ category', () => {
    const leaderboardData = {
      guitar: { seasonAchieved: 3 },
    } as unknown as LeaderboardData;
    renderSongRow({
      song: makeSong('s1', { instrumentKey: 'guitar' as any }),
      categoryKey: 'stale_guitar',
      leaderboardData,
    });
    expect(screen.getByText(/S3/)).toBeTruthy();
  });

  it('renders season pill with highest season when no specific instrument', () => {
    const leaderboardData = {
      guitar: { seasonAchieved: 2 },
      bass: { seasonAchieved: 4 },
    } as unknown as LeaderboardData;
    renderSongRow({
      song: makeSong('s1'),
      categoryKey: 'stale_all',
      leaderboardData,
    });
    expect(screen.getByText(/S4/)).toBeTruthy();
  });

  it('renders singleInstrument layout for near_fc category', () => {
    renderSongRow({
      song: makeSong('s1', { instrumentKey: 'drums' as any }),
      categoryKey: 'near_fc_drums',
    });
    expect(screen.getByTestId('instrument-drums')).toBeTruthy();
  });

  it('renders instrumentChips layout for generic categories', () => {
    const leaderboardData = {
      guitar: { numStars: 5, isFullCombo: true },
      bass: { numStars: 3, isFullCombo: false },
    } as unknown as LeaderboardData;
    renderSongRow({
      categoryKey: 'some_generic_cat',
      leaderboardData,
    });
    expect(screen.getByTestId('instrument-guitar')).toBeTruthy();
    expect(screen.getByTestId('instrument-bass')).toBeTruthy();
  });

  it('renders unfcAccuracy layout for unfc_ category', () => {
    renderSongRow({
      song: makeSong('s1', { percent: 97, instrumentKey: 'guitar' as any }),
      categoryKey: 'unfc_guitar',
    });
    expect(screen.getByText('97%')).toBeTruthy();
  });

  it('does not render accuracy when percent is 0', () => {
    renderSongRow({
      song: makeSong('s1', { percent: 0, instrumentKey: 'guitar' as any }),
      categoryKey: 'unfc_guitar',
    });
    expect(screen.queryByText('0%')).toBeNull();
  });

  it('renders star images for star_gains category on desktop', () => {
    const { container } = renderSongRow({
      song: makeSong('s1', { stars: 4, instrumentKey: 'guitar' as any }),
      categoryKey: 'star_gains_guitar',
    });
    // On desktop, stars render inline as img elements with star_*.png src
    const stars = container.querySelectorAll('img[src*="star_"]');
    expect(stars.length).toBeGreaterThanOrEqual(4);
  });

  it('renders album art from albumArtMap', () => {
    const { container } = renderSongRow({ albumArt: 'https://example.com/art.jpg' });
    const img = container.querySelector('img[src*="art.jpg"]');
    expect(img).toBeTruthy();
  });

  it('renders hidden layout for artist_sampler_ category', () => {
    const { container } = renderSongRow({ categoryKey: 'artist_sampler_drake' });
    expect(container.querySelector('[class*="badges"]')).toBeNull();
    expect(container.querySelector('[class*="instrumentChipsRow"]')).toBeNull();
  });

  it('renders hidden layout for artist_unplayed_ category', () => {
    const { container } = renderSongRow({ categoryKey: 'artist_unplayed_billie' });
    expect(container.querySelector('[class*="badges"]')).toBeNull();
    expect(container.querySelector('[class*="instrumentChipsRow"]')).toBeNull();
  });

  it('renders no season pill when leaderboardData is absent in stale_ category', () => {
    renderSongRow({
      song: makeSong('s1', { instrumentKey: 'guitar' as any }),
      categoryKey: 'stale_guitar',
      leaderboardData: undefined,
    });
    expect(screen.queryByText(/S\d/)).toBeNull();
  });

  it('renders instrumentChips without leaderboardData', () => {
    const { container } = renderSongRow({
      categoryKey: 'some_generic_cat',
      leaderboardData: undefined,
    });
    // All chips should render with no-score styling
    const chips = container.querySelectorAll('[class*="instrumentChip"]');
    expect(chips.length).toBeGreaterThan(0);
  });
});
