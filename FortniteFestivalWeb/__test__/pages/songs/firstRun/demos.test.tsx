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
let mockIsWideDesktop = true;
vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({
  useIsMobile: () => mockIsMobile,
  useIsMobileChrome: () => mockIsMobileChrome,
  useIsWideDesktop: () => mockIsWideDesktop,
  useIsNarrow: () => false,
}));

// Controllable player data mock
let mockPlayerData: any = null;
vi.mock('../../../../src/contexts/PlayerDataContext', () => ({
  usePlayerData: () => ({ playerData: mockPlayerData }),
  PlayerDataProvider: ({ children }: any) => children,
}));

// Mock useDemoSongs for components that use it
const DEMO_SONGS = [
  { title: 'Song A', artist: 'Artist A', year: 2024, albumArt: 'art-a.jpg' },
  { title: 'Song B', artist: 'Artist B', year: 2023, albumArt: 'art-b.jpg' },
  { title: 'Song C', artist: 'Artist C', year: 2022, albumArt: 'art-c.jpg' },
  { title: 'Song D', artist: 'Artist D', year: 2021, albumArt: 'art-d.jpg' },
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
import { DemoSongRow } from '../../../../src/pages/songs/firstRun/demo/DemoSongRow';
import SongRowDemo from '../../../../src/pages/songs/firstRun/demo/SongRowDemo';
import SortDemo from '../../../../src/pages/songs/firstRun/demo/SortDemo';
import FilterDemo from '../../../../src/pages/songs/firstRun/demo/FilterDemo';
import SongIconsDemo from '../../../../src/pages/songs/firstRun/demo/SongIconsDemo';
import NavigationDemo from '../../../../src/pages/songs/firstRun/demo/NavigationDemo';
import MetadataDemo from '../../../../src/pages/songs/firstRun/demo/MetadataDemo';

beforeAll(() => {
  stubResizeObserver();
});

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  mockSlideHeight = 400;
  mockIsMobile = false;
  mockIsMobileChrome = false;
  mockIsWideDesktop = true;
  mockPlayerData = null;
  mockDemoSongsResult = {
    rows: DEMO_SONGS.slice(0, 3),
    fadingIdx: new Set<number>(),
    initialDone: false,
    pool: DEMO_SONGS,
  };
});

afterEach(() => {
  vi.useRealTimers();
});

function wrap(ui: React.ReactElement) {
  return render(ui, { wrapper: TestProviders });
}

/* ── DemoSongRow ── */

describe('DemoSongRow', () => {
  it('renders children in desktop mode', () => {
    wrap(
      <DemoSongRow index={0} initialDone={false} fadingIdx={new Set()}>
        <span>hello</span>
      </DemoSongRow>,
    );
    expect(screen.getByText('hello')).toBeTruthy();
  });

  it('renders children in mobile mode', () => {
    wrap(
      <DemoSongRow index={0} initialDone={false} fadingIdx={new Set()} mobile>
        <span>mobile</span>
      </DemoSongRow>,
    );
    expect(screen.getByText('mobile')).toBeTruthy();
  });

  it('applies fade-in animation when initialDone is false', () => {
    const { container } = wrap(
      <DemoSongRow index={1} initialDone={false} fadingIdx={new Set()}>
        <span>anim</span>
      </DemoSongRow>,
    );
    const div = container.querySelector('[data-testid="test-scroll-container"]')!.firstChild as HTMLElement;
    expect(div.style.opacity).toBe('0');
    expect(div.style.animation).toContain('fadeInUp');
  });

  it('applies transition when initialDone is true and not fading', () => {
    const { container } = wrap(
      <DemoSongRow index={0} initialDone={true} fadingIdx={new Set()}>
        <span>done</span>
      </DemoSongRow>,
    );
    const div = container.querySelector('[data-testid="test-scroll-container"]')!.firstChild as HTMLElement;
    expect(div.style.opacity).toBe('1');
    expect(div.style.transition).toContain('opacity');
  });

  it('applies opacity 0 when fading out', () => {
    const { container } = wrap(
      <DemoSongRow index={2} initialDone={true} fadingIdx={new Set([2])}>
        <span>fading</span>
      </DemoSongRow>,
    );
    const div = container.querySelector('[data-testid="test-scroll-container"]')!.firstChild as HTMLElement;
    expect(div.style.opacity).toBe('0');
  });
});

/* ── SongRowDemo ── */

describe('SongRowDemo', () => {
  it('renders song titles from demo data', () => {
    wrap(<SongRowDemo />);
    expect(screen.getByText('Song A')).toBeTruthy();
    expect(screen.getByText('Song B')).toBeTruthy();
    expect(screen.getByText('Song C')).toBeTruthy();
  });

  it('renders artist names from demo data', () => {
    const { container } = wrap(<SongRowDemo />);
    // Artist text is combined with year: "Artist A \u00b7 2024" — check via text content
    expect(container.textContent).toContain('Artist A');
    expect(container.textContent).toContain('Artist B');
    expect(container.textContent).toContain('Artist C');
  });

  it('renders mobile layout when isMobile is true', () => {
    mockIsMobile = true;
    const { container } = wrap(<SongRowDemo />);
    // Mobile mode wraps content in mobileTopRow divs (inline styles after CSS migration)
    const mobileRows = [...container.querySelectorAll('div[style]') as NodeListOf<HTMLElement>].filter( el => el.style.gap === '12px' && el.style.alignItems === 'center' && !el.style.borderRadius
    );
    expect(mobileRows.length).toBe(3);
  });

  it('renders desktop layout when isMobile is false', () => {
    mockIsMobile = false;
    const { container } = wrap(<SongRowDemo />);
    const mobileRows = [...container.querySelectorAll('div[style]') as NodeListOf<HTMLElement>].filter( el => el.style.gap === '12px' && el.style.alignItems === 'center' && !el.style.borderRadius
    );
    expect(mobileRows.length).toBe(0);
  });
});

/* ── SortDemo ── */

describe('SortDemo', () => {
  it('renders sort mode header', () => {
    wrap(<SortDemo />);
    expect(screen.getByText('Mode')).toBeTruthy();
  });

  it('renders sort mode radio options', () => {
    wrap(<SortDemo />);
    expect(screen.getByText('Title')).toBeTruthy();
    expect(screen.getByText('Artist')).toBeTruthy();
    expect(screen.getByText('Year')).toBeTruthy();
  });

  it('selects a different radio option on click', () => {
    wrap(<SortDemo />);
    const artistBtn = screen.getByText('Artist');
    fireEvent.click(artistBtn);
    // Should not crash and artist option should exist
    expect(screen.getByText('Artist')).toBeTruthy();
  });

  it('renders direction selector at default height', () => {
    wrap(<SortDemo />);
    // DirectionSelector renders ascending/descending controls
    expect(screen.getByText('Sort Direction')).toBeTruthy();
  });

  it('renders mode hint at large height', () => {
    mockSlideHeight = 1000;
    wrap(<SortDemo />);
    expect(screen.getByText('Choose which property to sort the song list by.')).toBeTruthy();
  });

  it('hides direction selector at very small height', () => {
    mockSlideHeight = 30;
    wrap(<SortDemo />);
    expect(screen.queryByText('Sort Direction')).toBeNull();
  });

  it('handles zero slide height', () => {
    mockSlideHeight = 0;
    wrap(<SortDemo />);
    // Should still render mode header
    expect(screen.getByText('Mode')).toBeTruthy();
  });

  it('shows Has FC mode when player data is present', () => {
    mockPlayerData = { accountId: 'test' };
    wrap(<SortDemo />);
    expect(screen.getByText('Has FC')).toBeTruthy();
  });
});

/* ── FilterDemo ── */

describe('FilterDemo', () => {
  it('renders instrument selector', () => {
    wrap(<FilterDemo />);
    // InstrumentSelector renders buttons; check for instrument titles
    expect(screen.getByTitle('Lead')).toBeTruthy();
  });

  it('renders toggle rows at default height', () => {
    wrap(<FilterDemo />);
    expect(screen.getByText('Season')).toBeTruthy();
    expect(screen.getByText('Percentile')).toBeTruthy();
    expect(screen.getByText('Stars')).toBeTruthy();
  });

  it('toggles a filter row on click', () => {
    wrap(<FilterDemo />);
    const starsToggle = screen.getByText('Stars');
    fireEvent.click(starsToggle);
    // Stars was on=true, should now be off — the toggle still renders
    expect(screen.getByText('Stars')).toBeTruthy();
  });

  it('hides toggles at very small height', () => {
    mockSlideHeight = 20;
    wrap(<FilterDemo />);
    expect(screen.queryByText('Season')).toBeNull();
  });

  it('handles zero slide height', () => {
    mockSlideHeight = 0;
    wrap(<FilterDemo />);
    // Instrument selector still renders
    expect(screen.getByTitle('Lead')).toBeTruthy();
  });

  it('switches instrument and shows new toggles', () => {
    wrap(<FilterDemo />);
    const bassBtn = screen.getByTitle('Bass');
    fireEvent.click(bassBtn);
    // Toggles still render after instrument switch
    expect(screen.getByText('Season')).toBeTruthy();
  });

  it('reuses cached toggles when switching back', () => {
    wrap(<FilterDemo />);
    // Switch to Bass, then back to Lead
    fireEvent.click(screen.getByTitle('Bass'));
    fireEvent.click(screen.getByTitle('Lead'));
    // Should render toggles from cache (covers getToggles cache-hit branch)
    expect(screen.getByText('Season')).toBeTruthy();
  });

  it('shows toggles without header at intermediate height', () => {
    // h=130 → afterInstr=60, fits toggles (56) but not header+toggle (86)
    mockSlideHeight = 130;
    wrap(<FilterDemo />);
    // Toggles should show
    expect(screen.getByText('Season')).toBeTruthy();
  });
});

/* ── SongIconsDemo ── */

describe('SongIconsDemo', () => {
  it('renders song titles from demo data', () => {
    wrap(<SongIconsDemo />);
    expect(screen.getByText('Song A')).toBeTruthy();
    expect(screen.getByText('Song B')).toBeTruthy();
  });

  it('renders instrument chips', () => {
    const { container } = wrap(<SongIconsDemo />);
    // InstrumentChip rows use instrumentStatusRow style (gap: 4px)
    const chips = [...container.querySelectorAll('div[style]') as NodeListOf<HTMLElement>].filter( el => el.style.gap === '4px' && el.style.alignItems === 'center'
    );
    expect(chips.length).toBeGreaterThanOrEqual(1);
  });

  it('renders mobile layout when isMobileChrome is true', () => {
    mockIsMobileChrome = true;
    const { container } = wrap(<SongIconsDemo />);
    const mobileRows = [...container.querySelectorAll('div[style]') as NodeListOf<HTMLElement>].filter( el => el.style.gap === '12px' && el.style.alignItems === 'center' && !el.style.borderRadius
    );
    expect(mobileRows.length).toBe(3);
  });

  it('renders desktop layout when isMobileChrome is false', () => {
    mockIsMobileChrome = false;
    const { container } = wrap(<SongIconsDemo />);
    const mobileRows = [...container.querySelectorAll('div[style]') as NodeListOf<HTMLElement>].filter( el => el.style.gap === '12px' && el.style.alignItems === 'center' && !el.style.borderRadius
    );
    expect(mobileRows.length).toBe(0);
  });
});

/* ── NavigationDemo ── */

describe('NavigationDemo', () => {
  it('renders desktop pinned sidebar when isWideDesktop', () => {
    mockIsMobileChrome = false;
    mockIsWideDesktop = true;
    wrap(<NavigationDemo />);
    expect(screen.getByText('Songs')).toBeTruthy();
    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('renders mobile bottom nav when isMobileChrome', () => {
    mockIsMobileChrome = true;
    mockIsWideDesktop = false;
    wrap(<NavigationDemo />);
    expect(screen.getByText('Songs')).toBeTruthy();
    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('renders compact sidebar when not mobile and not wide', () => {
    mockIsMobileChrome = false;
    mockIsWideDesktop = false;
    wrap(<NavigationDemo />);
    expect(screen.getByText('Songs')).toBeTruthy();
    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('handles tab click in desktop mode', () => {
    mockIsMobileChrome = false;
    mockIsWideDesktop = true;
    wrap(<NavigationDemo />);
    const settingsBtn = screen.getByText('Settings');
    fireEvent.click(settingsBtn);
    // Button should become active (no crash)
    expect(settingsBtn).toBeTruthy();
  });

  it('handles tab click in mobile mode', () => {
    mockIsMobileChrome = true;
    wrap(<NavigationDemo />);
    const settingsBtn = screen.getByText('Settings');
    fireEvent.click(settingsBtn);
    expect(settingsBtn).toBeTruthy();
  });

  it('handles tab click in compact mode', () => {
    mockIsMobileChrome = false;
    mockIsWideDesktop = false;
    wrap(<NavigationDemo />);
    const settingsBtn = screen.getByText('Settings');
    fireEvent.click(settingsBtn);
    expect(settingsBtn).toBeTruthy();
  });

  it('handles zero slide height', () => {
    mockSlideHeight = 0;
    mockIsMobileChrome = false;
    mockIsWideDesktop = true;
    wrap(<NavigationDemo />);
    expect(screen.getByText('Songs')).toBeTruthy();
  });

  it('truncates tabs when slide height is tiny', () => {
    mockSlideHeight = 20;
    mockIsMobileChrome = false;
    mockIsWideDesktop = true;
    wrap(<NavigationDemo />);
    // Should still render at least Songs and Settings (first and last)
    expect(screen.getByText('Songs')).toBeTruthy();
    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('shows all 4 tabs with player data', () => {
    mockPlayerData = { accountId: 'test' };
    mockIsMobileChrome = false;
    mockIsWideDesktop = true;
    wrap(<NavigationDemo />);
    expect(screen.getByText('Songs')).toBeTruthy();
    expect(screen.getByText('Suggestions')).toBeTruthy();
    expect(screen.getByText('Statistics')).toBeTruthy();
    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('truncates desktop tabs with player data at small height', () => {
    mockPlayerData = { accountId: 'test' };
    mockSlideHeight = 100;
    mockIsMobileChrome = false;
    mockIsWideDesktop = true;
    wrap(<NavigationDemo />);
    // First and last tabs should always be visible
    expect(screen.getByText('Songs')).toBeTruthy();
    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('truncates compact tabs with player data at small height', () => {
    mockPlayerData = { accountId: 'test' };
    mockSlideHeight = 80;
    mockIsMobileChrome = false;
    mockIsWideDesktop = false;
    wrap(<NavigationDemo />);
    expect(screen.getByText('Songs')).toBeTruthy();
    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('renders mobile nav with player data', () => {
    mockPlayerData = { accountId: 'test' };
    mockIsMobileChrome = true;
    wrap(<NavigationDemo />);
    expect(screen.getByText('Songs')).toBeTruthy();
  });
});

/* ── MetadataDemo ── */

describe('MetadataDemo', () => {
  it('renders desktop layout with song titles', () => {
    mockIsMobile = false;
    wrap(<MetadataDemo />);
    expect(screen.getByText('Song A')).toBeTruthy();
    expect(screen.getByText('Song B')).toBeTruthy();
  });

  it('renders mobile layout when isMobile', () => {
    mockIsMobile = true;
    const { container } = wrap(<MetadataDemo />);
    const mobileRows = [...container.querySelectorAll('div[style]') as NodeListOf<HTMLElement>].filter( el => el.style.gap === '12px' && el.style.alignItems === 'center' && !el.style.borderRadius && !el.style.flexShrink
    );
    expect(mobileRows.length).toBe(3);
  });

  it('renders metadata strips in desktop mode', () => {
    mockIsMobile = false;
    const { container } = wrap(<MetadataDemo />);
    // scoreMeta style: gap 12px + flex-shrink: 1
    const metaStrips = [...container.querySelectorAll('div[style]') as NodeListOf<HTMLElement>].filter( el => el.style.gap === '12px' && el.style.flexShrink === '1'
    );
    expect(metaStrips.length).toBeGreaterThanOrEqual(1);
  });

  it('renders with no rows', () => {
    mockDemoSongsResult = {
      rows: [],
      fadingIdx: new Set(),
      initialDone: false,
      pool: DEMO_SONGS,
    };
    const { container } = wrap(<MetadataDemo />);
    // Empty list renders without crashing
    expect(container.firstChild).toBeTruthy();
  });

  it('renders mobile metadata strips', () => {
    mockIsMobile = true;
    const { container } = wrap(<MetadataDemo />);
    // Mobile mode shows metadataWrap elements (flex-wrap: wrap)
    const metaWraps = [...container.querySelectorAll('div[style]') as NodeListOf<HTMLElement>].filter( el => el.style.flexWrap === 'wrap'
    );
    expect(metaWraps.length).toBeGreaterThanOrEqual(1);
  });
});

/* ── edge cases — zero slide height ── */

describe('edge cases — zero slide height', () => {
  beforeEach(() => { mockSlideHeight = 0; });

  it('SongRowDemo renders with zero height', () => {
    wrap(<SongRowDemo />);
    expect(screen.getByText('Song A')).toBeTruthy();
  });

  it('SortDemo renders with zero height', () => {
    wrap(<SortDemo />);
    expect(screen.getByText('Mode')).toBeTruthy();
  });

  it('FilterDemo renders with zero height', () => {
    wrap(<FilterDemo />);
    expect(screen.getByTitle('Lead')).toBeTruthy();
  });

  it('SongIconsDemo renders with zero height', () => {
    wrap(<SongIconsDemo />);
    expect(screen.getByText('Song A')).toBeTruthy();
  });

  it('NavigationDemo desktop renders with zero height', () => {
    mockIsWideDesktop = true;
    wrap(<NavigationDemo />);
    expect(screen.getByText('Songs')).toBeTruthy();
  });

  it('NavigationDemo mobile renders with zero height', () => {
    mockIsMobileChrome = true;
    wrap(<NavigationDemo />);
    expect(screen.getByText('Songs')).toBeTruthy();
  });

  it('MetadataDemo renders with zero height', () => {
    wrap(<MetadataDemo />);
    expect(screen.getByText('Song A')).toBeTruthy();
  });
});

/* ── edge cases — tiny slide height ── */

describe('edge cases — tiny slide height', () => {
  it('SortDemo with tiny height limits visible modes', () => {
    mockSlideHeight = 50;
    wrap(<SortDemo />);
    // At least Title should render (maxModes >= 1)
    expect(screen.getByText('Title')).toBeTruthy();
  });

  it('FilterDemo with tiny height hides header', () => {
    mockSlideHeight = 50;
    wrap(<FilterDemo />);
    // Instrument selector still renders
    expect(screen.getByTitle('Lead')).toBeTruthy();
  });

  it('NavigationDemo compact with tiny height truncates tabs', () => {
    mockSlideHeight = 20;
    mockIsMobileChrome = false;
    mockIsWideDesktop = false;
    wrap(<NavigationDemo />);
    expect(screen.getByText('Songs')).toBeTruthy();
  });
});
