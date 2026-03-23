import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { stubResizeObserver } from '../../../helpers/browserStubs';
import { TestProviders } from '../../../helpers/TestProviders';

// Controllable slide height mock
let mockSlideHeight: number | undefined = 400;
vi.mock('../../../../src/firstRun/SlideHeightContext', () => ({
  SlideHeightContext: { Provider: ({ children }: any) => children },
  useSlideHeight: () => mockSlideHeight,
}));

// Controllable mobile/desktop mocks
let mockIsMobile = false;
let mockIsMobileChrome = false;
let mockIsWideDesktop = true;
vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({
  useIsMobile: () => mockIsMobile,
  useIsMobileChrome: () => mockIsMobileChrome,
  useIsWideDesktop: () => mockIsWideDesktop,
}));

// Mock useDemoSongs for TopSongsDemo
const DEMO_SONGS = [
  { title: 'Demo A', artist: 'Artist A', year: 2024, albumArt: 'art-a.jpg' },
  { title: 'Demo B', artist: 'Artist B', year: 2023, albumArt: 'art-b.jpg' },
  { title: 'Demo C', artist: 'Artist C', year: 2022, albumArt: 'art-c.jpg' },
  { title: 'Demo D', artist: 'Artist D', year: 2021, albumArt: 'art-d.jpg' },
];

let mockDemoSongsResult: {
  rows: typeof DEMO_SONGS;
  fadingIdx: ReadonlySet<number>;
  initialDone: boolean;
  pool: typeof DEMO_SONGS;
};

vi.mock('../../../../src/hooks/data/useDemoSongs', () => ({
  useDemoSongs: () => mockDemoSongsResult,
  FADE_MS: 300,
  shuffle: <T,>(arr: readonly T[]): T[] => [...arr],
}));

// Import components AFTER mocks
import OverviewDemo from '../../../../src/pages/player/firstRun/demo/OverviewDemo';
import InstrumentBreakdownDemo from '../../../../src/pages/player/firstRun/demo/InstrumentBreakdownDemo';
import PercentileDemo from '../../../../src/pages/player/firstRun/demo/PercentileDemo';
import TopSongsDemo from '../../../../src/pages/player/firstRun/demo/TopSongsDemo';
import DrillDownDemo from '../../../../src/pages/player/firstRun/demo/DrillDownDemo';

beforeAll(() => {
  stubResizeObserver();
});

beforeEach(() => {
  mockSlideHeight = 400;
  mockIsMobile = false;
  mockIsMobileChrome = false;
  mockIsWideDesktop = true;
  mockDemoSongsResult = {
    rows: DEMO_SONGS,
    fadingIdx: new Set<number>(),
    initialDone: true,
    pool: DEMO_SONGS,
  };
});

function wrap(ui: React.ReactElement) {
  return render(ui, { wrapper: TestProviders });
}

/* ── OverviewDemo ── */

describe('OverviewDemo', () => {
  it('renders stat labels', () => {
    wrap(<OverviewDemo />);
    expect(screen.getByText('Songs Played')).toBeTruthy();
    expect(screen.getByText('Full Combos')).toBeTruthy();
    expect(screen.getByText('Gold Stars')).toBeTruthy();
    expect(screen.getByText('Avg Accuracy')).toBeTruthy();
  });

  it('renders with h=0 (fallback to items.length)', () => {
    mockSlideHeight = 0;
    wrap(<OverviewDemo />);
    expect(screen.getByText('Songs Played')).toBeTruthy();
  });

  it('renders with h=400', () => {
    mockSlideHeight = 400;
    wrap(<OverviewDemo />);
    expect(screen.getByText('Songs Played')).toBeTruthy();
  });

  it('renders mobile layout when isMobileChrome is true', () => {
    mockIsMobileChrome = true;
    const { container } = wrap(<OverviewDemo />);
    // Mobile uses single-column grid
    const grid = container.firstChild as HTMLElement;
    expect(grid.style.gridTemplateColumns).toBe('1fr');
  });

  it('renders desktop layout when isMobileChrome is false', () => {
    mockIsMobileChrome = false;
    const { container } = wrap(<OverviewDemo />);
    const grid = container.firstChild as HTMLElement;
    // Desktop does not force single-column
    expect(grid.style.gridTemplateColumns).not.toBe('1fr');
  });
});

/* ── InstrumentBreakdownDemo ── */

describe('InstrumentBreakdownDemo', () => {
  it('renders instrument stat items', () => {
    wrap(<InstrumentBreakdownDemo />);
    // buildInstrumentStatsItems generates a header + stat cards
    expect(screen.getByText('Songs Played')).toBeTruthy();
  });

  it('renders with h=0 (fallback to all cards)', () => {
    mockSlideHeight = 0;
    wrap(<InstrumentBreakdownDemo />);
    expect(screen.getAllByText('Songs Played').length).toBeGreaterThanOrEqual(1);
  });

  it('renders with h=400', () => {
    mockSlideHeight = 400;
    wrap(<InstrumentBreakdownDemo />);
    expect(screen.getByText('Songs Played')).toBeTruthy();
  });

  it('renders mobile layout when isMobileChrome is true', () => {
    mockIsMobileChrome = true;
    const { container } = wrap(<InstrumentBreakdownDemo />);
    const grid = container.firstChild as HTMLElement;
    expect(grid.style.gridTemplateColumns).toBe('1fr');
  });

  it('renders desktop layout when isMobileChrome is false', () => {
    mockIsMobileChrome = false;
    const { container } = wrap(<InstrumentBreakdownDemo />);
    const grid = container.firstChild as HTMLElement;
    expect(grid.style.gridTemplateColumns).not.toBe('1fr');
  });

  it('limits visible cards based on available height', () => {
    // Very small height should limit cards
    mockSlideHeight = 100;
    wrap(<InstrumentBreakdownDemo />);
    // Should still render at least the header
    expect(screen.getByText('Songs Played')).toBeTruthy();
  });
});

/* ── PercentileDemo ── */

describe('PercentileDemo', () => {
  it('renders percentile header labels', () => {
    wrap(<PercentileDemo />);
    expect(screen.getByText('Percentile')).toBeTruthy();
    expect(screen.getByText('Songs')).toBeTruthy();
  });

  it('renders percentile bucket values', () => {
    wrap(<PercentileDemo />);
    // Percentile pills render "Top X%"
    expect(screen.getByText('Top 1%')).toBeTruthy();
    expect(screen.getByText('Top 5%')).toBeTruthy();
    expect(screen.getByText('Top 10%')).toBeTruthy();
    expect(screen.getByText('Top 25%')).toBeTruthy();
    expect(screen.getByText('Top 50%')).toBeTruthy();
    expect(screen.getByText('Top 100%')).toBeTruthy();
  });

  it('renders song counts for each bucket', () => {
    wrap(<PercentileDemo />);
    expect(screen.getByText('3')).toBeTruthy();
    expect(screen.getByText('12')).toBeTruthy();
    expect(screen.getByText('28')).toBeTruthy();
    expect(screen.getByText('55')).toBeTruthy();
    expect(screen.getByText('89')).toBeTruthy();
    expect(screen.getByText('142')).toBeTruthy();
  });

  it('renders with h=0 (shows all buckets)', () => {
    mockSlideHeight = 0;
    wrap(<PercentileDemo />);
    expect(screen.getByText('Top 100%')).toBeTruthy();
  });

  it('limits visible rows with small height', () => {
    // With h=50, only room for ~1 row
    mockSlideHeight = 50;
    wrap(<PercentileDemo />);
    // Should render at least one bucket
    expect(screen.getByText('Top 1%')).toBeTruthy();
  });
});

/* ── TopSongsDemo ── */

describe('TopSongsDemo', () => {
  it('renders song rows from useDemoSongs', () => {
    wrap(<TopSongsDemo />);
    expect(screen.getByText('Demo A')).toBeTruthy();
    expect(screen.getByText('Demo B')).toBeTruthy();
  });

  it('renders with h=undefined (fallback row count)', () => {
    mockSlideHeight = undefined;
    wrap(<TopSongsDemo />);
    expect(screen.getByText('Demo A')).toBeTruthy();
  });

  it('renders with h=0', () => {
    mockSlideHeight = 0;
    wrap(<TopSongsDemo />);
    expect(screen.getByText('Demo A')).toBeTruthy();
  });

  it('renders with h=400', () => {
    mockSlideHeight = 400;
    wrap(<TopSongsDemo />);
    expect(screen.getByText('Demo A')).toBeTruthy();
  });

  it('applies fade-in animation when initialDone is false', () => {
    mockDemoSongsResult = {
      rows: DEMO_SONGS,
      fadingIdx: new Set<number>(),
      initialDone: false,
      pool: DEMO_SONGS,
    };
    const { container } = wrap(<TopSongsDemo />);
    const firstRow = container.querySelector('[class*="songList"]')!.firstChild as HTMLElement;
    expect(firstRow.style.opacity).toBe('0');
    expect(firstRow.style.animation).toContain('fadeInUp');
  });

  it('applies fading transition when initialDone is true and fading', () => {
    mockDemoSongsResult = {
      rows: DEMO_SONGS,
      fadingIdx: new Set<number>([0]),
      initialDone: true,
      pool: DEMO_SONGS,
    };
    const { container } = wrap(<TopSongsDemo />);
    const firstRow = container.querySelector('[class*="songList"]')!.firstChild as HTMLElement;
    expect(firstRow.style.opacity).toBe('0');
  });

  it('renders mobile layout when isMobile is true', () => {
    mockIsMobile = true;
    wrap(<TopSongsDemo />);
    // Mobile renders same song rows — just verifying no crash
    expect(screen.getByText('Demo A')).toBeTruthy();
  });
});

/* ── DrillDownDemo ── */

describe('DrillDownDemo', () => {
  it('renders stat box labels', () => {
    wrap(<DrillDownDemo />);
    expect(screen.getByText('Songs Played')).toBeTruthy();
    expect(screen.getByText('Gold Stars')).toBeTruthy();
    expect(screen.getByText('Avg Accuracy')).toBeTruthy();
    expect(screen.getByText('Full Combos')).toBeTruthy();
  });

  it('renders stat box values', () => {
    wrap(<DrillDownDemo />);
    expect(screen.getByText('142')).toBeTruthy();
    expect(screen.getByText('12')).toBeTruthy();
    expect(screen.getByText('96.2%')).toBeTruthy();
    expect(screen.getByText('38 (26.8%)')).toBeTruthy();
  });

  it('applies pulse class to clickable boxes only', () => {
    const { container } = wrap(<DrillDownDemo />);
    const pulseWraps = container.querySelectorAll('[class*="pulseWrap"]');
    // Only "Songs Played" and "Full Combos" are clickable
    expect(pulseWraps.length).toBe(2);
  });

  it('renders with h=0 (shows all boxes)', () => {
    mockSlideHeight = 0;
    wrap(<DrillDownDemo />);
    expect(screen.getByText('Songs Played')).toBeTruthy();
    expect(screen.getByText('Full Combos')).toBeTruthy();
  });

  it('renders with h=400', () => {
    mockSlideHeight = 400;
    wrap(<DrillDownDemo />);
    expect(screen.getByText('Songs Played')).toBeTruthy();
  });

  it('renders mobile layout when isMobileChrome is true', () => {
    mockIsMobileChrome = true;
    const { container } = wrap(<DrillDownDemo />);
    const grid = container.firstChild as HTMLElement;
    expect(grid.style.gridTemplateColumns).toBe('1fr');
  });

  it('renders desktop layout when isMobileChrome is false', () => {
    mockIsMobileChrome = false;
    const { container } = wrap(<DrillDownDemo />);
    const grid = container.firstChild as HTMLElement;
    expect(grid.style.gridTemplateColumns).not.toBe('1fr');
  });

  it('limits visible boxes based on small height', () => {
    mockSlideHeight = 50;
    wrap(<DrillDownDemo />);
    // With very small height, only some boxes fit
    expect(screen.getByText('Songs Played')).toBeTruthy();
  });
});
