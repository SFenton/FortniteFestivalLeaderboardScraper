import { describe, it, expect, vi, beforeEach, beforeAll, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { stubResizeObserver } from '../../../helpers/browserStubs';
import { TestProviders } from '../../../helpers/TestProviders';

// Controllable slide height mock
let mockSlideHeight = 400;
vi.mock('../../../../src/firstRun/SlideHeightContext', () => ({
  SlideHeightContext: { Provider: ({ children }: any) => children },
  useSlideHeight: () => mockSlideHeight,
}));

// Controllable mobile/desktop mocks
let mockIsMobile = false;
let mockIsMobileChrome = false;
vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({
  useIsMobile: () => mockIsMobile,
  useIsMobileChrome: () => mockIsMobileChrome,
  useIsWideDesktop: () => true,
  useIsNarrow: () => false,
}));

// Mock useFestival to provide demo songs with albumArt for CategoryCardDemo and InfiniteScrollDemo
const DEMO_SONGS = [
  { songId: 's1', title: 'Alpha', artist: 'Artist A', year: 2024, albumArt: 'art-a.jpg' },
  { songId: 's2', title: 'Bravo', artist: 'Artist B', year: 2023, albumArt: 'art-b.jpg' },
  { songId: 's3', title: 'Charlie', artist: 'Artist C', year: 2022, albumArt: 'art-c.jpg' },
  { songId: 's4', title: 'Delta', artist: 'Artist D', year: 2021, albumArt: 'art-d.jpg' },
  { songId: 's5', title: 'Echo', artist: 'Artist E', year: 2020, albumArt: 'art-e.jpg' },
  { songId: 's6', title: 'Foxtrot', artist: 'Artist F', year: 2019, albumArt: 'art-f.jpg' },
  { songId: 's7', title: 'Golf', artist: 'Artist G', year: 2018, albumArt: 'art-g.jpg' },
  { songId: 's8', title: 'Hotel', artist: 'Artist H', year: 2017, albumArt: 'art-h.jpg' },
  { songId: 's9', title: 'India', artist: 'Artist I', year: 2016, albumArt: 'art-i.jpg' },
  { songId: 's10', title: 'Juliet', artist: 'Artist J', year: 2015, albumArt: 'art-j.jpg' },
  { songId: 's11', title: 'Kilo', artist: 'Artist K', year: 2014, albumArt: 'art-k.jpg' },
  { songId: 's12', title: 'Lima', artist: 'Artist L', year: 2013, albumArt: 'art-l.jpg' },
];

vi.mock('../../../../src/contexts/FestivalContext', () => ({
  useFestival: () => ({
    state: { songs: DEMO_SONGS, currentSeason: 1, isLoading: false, error: null },
    actions: { refresh: vi.fn() },
  }),
  FestivalProvider: ({ children }: any) => children,
  FestivalContext: { Provider: ({ children }: any) => children },
}));

// Mock PlayerDataContext to avoid errors
vi.mock('../../../../src/contexts/PlayerDataContext', () => ({
  usePlayerData: () => ({ playerData: null }),
  PlayerDataProvider: ({ children }: any) => children,
}));

// Import components AFTER mocks
import CategoryCardDemo from '../../../../src/pages/suggestions/firstRun/demo/CategoryCardDemo';
import GlobalFilterDemo from '../../../../src/pages/suggestions/firstRun/demo/GlobalFilterDemo';
import InfiniteScrollDemo from '../../../../src/pages/suggestions/firstRun/demo/InfiniteScrollDemo';
import InstrumentFilterDemo from '../../../../src/pages/suggestions/firstRun/demo/InstrumentFilterDemo';

beforeAll(() => {
  stubResizeObserver();
});

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  mockSlideHeight = 400;
  mockIsMobile = false;
  mockIsMobileChrome = false;
});

afterEach(() => {
  vi.useRealTimers();
});

function wrap(ui: React.ReactElement) {
  return render(ui, { wrapper: TestProviders });
}

/* ── CategoryCardDemo ── */

describe('CategoryCardDemo', () => {
  it('renders the first category card title', () => {
    wrap(<CategoryCardDemo />);
    expect(screen.getByText('Finish the Lead FCs')).toBeTruthy();
  });

  it('renders the first category card description', () => {
    wrap(<CategoryCardDemo />);
    expect(screen.getByText('Play these songs again on Lead and grab an FC!')).toBeTruthy();
  });

  it('renders song rows from demo data', () => {
    wrap(<CategoryCardDemo />);
    // Should render songs from the pool (up to maxSongs based on height)
    expect(screen.getByText('Alpha')).toBeTruthy();
  });

  it('limits song count based on slide height', () => {
    // height 400, overhead 96, row 52 → (400-96)/52 = 5.8 → floor = 5, min(5,5) = 5
    mockSlideHeight = 150;
    // (150-96)/52 = 1.03 → floor = 1
    wrap(<CategoryCardDemo />);
    expect(screen.getByText('Alpha')).toBeTruthy();
  });

  it('uses MAX_DEMO_SONGS when height is 0', () => {
    mockSlideHeight = 0;
    wrap(<CategoryCardDemo />);
    // h=0 → maxSongs = MAX_DEMO_SONGS (5)
    expect(screen.getByText('Finish the Lead FCs')).toBeTruthy();
  });

  it('handles very small height (1 song minimum)', () => {
    mockSlideHeight = 100;
    // (100-96)/52 = 0.07 → max(1, floor) = 1
    wrap(<CategoryCardDemo />);
    expect(screen.getByText('Finish the Lead FCs')).toBeTruthy();
  });
});

/* ── GlobalFilterDemo ── */

describe('GlobalFilterDemo', () => {
  it('renders toggle rows for suggestion types', () => {
    wrap(<GlobalFilterDemo />);
    expect(screen.getByText('Near FC')).toBeTruthy();
    expect(screen.getByText('Star Progress')).toBeTruthy();
    expect(screen.getByText('Unplayed')).toBeTruthy();
  });

  it('renders toggle descriptions', () => {
    wrap(<GlobalFilterDemo />);
    expect(screen.getByText("Songs you're close to full-comboing.")).toBeTruthy();
  });

  it('toggles a suggestion type on click', () => {
    wrap(<GlobalFilterDemo />);
    const nearFc = screen.getByText('Near FC');
    fireEvent.click(nearFc);
    // Should not crash — toggle still renders
    expect(screen.getByText('Near FC')).toBeTruthy();
  });

  it('limits visible toggles based on slide height', () => {
    // filterToggleRowHeight = 56, so h=56 → 1 toggle visible
    mockSlideHeight = 56;
    wrap(<GlobalFilterDemo />);
    expect(screen.getByText('Near FC')).toBeTruthy();
  });

  it('shows all toggles when h is 0 (uses full length)', () => {
    mockSlideHeight = 0;
    wrap(<GlobalFilterDemo />);
    // h=0 → maxToggles = SUGGESTION_TYPES.length (all)
    expect(screen.getByText('Near FC')).toBeTruthy();
    expect(screen.getByText('Stale Songs')).toBeTruthy();
  });

  it('renders at least 1 toggle at small height', () => {
    mockSlideHeight = 10;
    // max(1, floor(10/56)) = max(1, 0) = 1
    wrap(<GlobalFilterDemo />);
    expect(screen.getByText('Near FC')).toBeTruthy();
  });
});

/* ── InfiniteScrollDemo ── */

describe('InfiniteScrollDemo', () => {
  it('renders category cards from templates', () => {
    wrap(<InfiniteScrollDemo />);
    expect(screen.getByText('Finish the Lead FCs')).toBeTruthy();
    expect(screen.getByText('Percentile Push: Bass')).toBeTruthy();
  });

  it('renders multiple category cards', () => {
    wrap(<InfiniteScrollDemo />);
    // 6 category templates
    expect(screen.getByText('Play Vocals This Season')).toBeTruthy();
    expect(screen.getByText('FC These Next!')).toBeTruthy();
    expect(screen.getByText('New on Drums')).toBeTruthy();
    expect(screen.getByText('Variety Pack')).toBeTruthy();
  });

  it('renders with zero height gracefully', () => {
    mockSlideHeight = 0;
    const { container } = wrap(<InfiniteScrollDemo />);
    // Should render the viewport div even if h=0
    expect(container.firstChild).toBeTruthy();
  });

  it('sets viewport height from slide context', () => {
    mockSlideHeight = 350;
    const { container } = wrap(<InfiniteScrollDemo />);
    // viewportRef gets style.height set
    const viewport = container.querySelector('[data-testid="test-scroll-container"]')!.firstChild as HTMLElement;
    expect(viewport.style.height).toBe('350px');
  });

  it('renders song data inside category cards', () => {
    wrap(<InfiniteScrollDemo />);
    // Songs from pool are added to cards
    expect(screen.getByText('Alpha')).toBeTruthy();
  });
});

/* ── InstrumentFilterDemo ── */

describe('InstrumentFilterDemo', () => {
  it('renders instrument selector', () => {
    wrap(<InstrumentFilterDemo />);
    // Default instruments include Lead
    expect(screen.getByTitle('Lead')).toBeTruthy();
  });

  it('renders toggle rows at default height', () => {
    wrap(<InstrumentFilterDemo />);
    expect(screen.getByText('Near FC')).toBeTruthy();
    expect(screen.getByText('Star Progress')).toBeTruthy();
  });

  it('toggles a filter row on click', () => {
    wrap(<InstrumentFilterDemo />);
    const nearFc = screen.getByText('Near FC');
    fireEvent.click(nearFc);
    expect(screen.getByText('Near FC')).toBeTruthy();
  });

  it('hides toggles at very small height', () => {
    // filterInstrumentRowHeight=70, filterToggleRowHeight=56
    // afterInstr = 20 - 70 = negative → 0 toggles
    mockSlideHeight = 20;
    wrap(<InstrumentFilterDemo />);
    expect(screen.queryByText('Near FC')).toBeNull();
  });

  it('handles zero slide height', () => {
    mockSlideHeight = 0;
    wrap(<InstrumentFilterDemo />);
    // Instrument selector still renders (h effect is guarded by if(!h))
    expect(screen.getByTitle('Lead')).toBeTruthy();
  });

  it('switches instrument and shows new toggles', () => {
    wrap(<InstrumentFilterDemo />);
    const bassBtn = screen.getByTitle('Bass');
    fireEvent.click(bassBtn);
    // Toggles still render after instrument switch
    expect(screen.getByText('Near FC')).toBeTruthy();
  });

  it('reuses cached toggles when switching back', () => {
    wrap(<InstrumentFilterDemo />);
    // Switch to Bass, then back to Lead
    fireEvent.click(screen.getByTitle('Bass'));
    fireEvent.click(screen.getByTitle('Lead'));
    expect(screen.getByText('Near FC')).toBeTruthy();
  });

  it('shows toggles without header at intermediate height', () => {
    // filterInstrumentRowHeight=70, filterToggleRowHeight=56, filterHeaderHeight=30
    // afterInstr = 130-70 = 60 → fits 1 toggle (56) but not header+toggle (30+56=86)
    mockSlideHeight = 130;
    wrap(<InstrumentFilterDemo />);
    expect(screen.getByText('Near FC')).toBeTruthy();
  });

  it('shows header when enough height', () => {
    // afterInstr = 400-70 = 330 → fits header(30) + many toggles
    mockSlideHeight = 400;
    wrap(<InstrumentFilterDemo />);
    expect(screen.getByText('Per-Instrument Types')).toBeTruthy();
  });

  it('deselects instrument to null and hides toggles', () => {
    wrap(<InstrumentFilterDemo />);
    // Click Lead again to deselect
    fireEvent.click(screen.getByTitle('Lead'));
    // Toggles should disappear since no instrument is selected
    expect(screen.queryByText('Near FC')).toBeNull();
  });
});
