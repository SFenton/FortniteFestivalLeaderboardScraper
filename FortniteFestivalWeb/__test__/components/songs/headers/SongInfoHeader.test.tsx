import { describe, it, expect, vi, beforeAll } from 'vitest';
import { render, screen } from '@testing-library/react';
import { TestProviders } from '../../../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubElementDimensions, stubIntersectionObserver } from '../../../helpers/browserStubs';

vi.mock('../../../../src/api/client', () => ({
  api: {
    searchAccounts: vi.fn().mockResolvedValue({ results: [] }),
    getPlayerHistory: vi.fn().mockResolvedValue({ accountId: 'a1', count: 0, history: [] }),
    getSongs: vi.fn().mockResolvedValue({ songs: [], count: 0, currentSeason: 5 }),
    getVersion: vi.fn().mockResolvedValue({ version: '1.0.0' }),
    getSyncStatus: vi.fn().mockResolvedValue({ accountId: '', isTracked: false, backfill: null, historyRecon: null }),
    getPlayer: vi.fn().mockResolvedValue(null),
    getPlayerStats: vi.fn().mockResolvedValue({ accountId: 'p1', stats: [] }),
    trackPlayer: vi.fn().mockResolvedValue({ accountId: 'p1', displayName: 'P', trackingStarted: false, backfillStatus: 'none' }),
  },
}));

vi.mock('react-icons/io5', () => {
  const Stub = (p: any) => <span data-testid={p['aria-label'] ?? 'icon'} />;
  return {
    IoMenu: Stub, IoClose: Stub, IoArrowUp: Stub, IoArrowDown: Stub,
    IoPerson: Stub, IoSearch: Stub, IoFilter: Stub, IoSwapVertical: Stub,
    IoMusicalNotes: Stub, IoChevronBack: Stub, IoEllipsisVertical: Stub,
    IoSettingsSharp: Stub, IoRefresh: Stub, IoAdd: Stub, IoRemove: Stub,
    IoCheckmarkCircle: Stub, IoAlertCircle: Stub,
  };
});

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
  stubIntersectionObserver();
  if (!HTMLElement.prototype.animate) {
    HTMLElement.prototype.animate = vi.fn().mockReturnValue({ cancel: vi.fn(), pause: vi.fn(), play: vi.fn(), finish: vi.fn(), onfinish: null, finished: Promise.resolve() }) as any;
  }
  if (!HTMLElement.prototype.getAnimations) {
    HTMLElement.prototype.getAnimations = vi.fn().mockReturnValue([]) as any;
  }
});

import SongInfoHeader from '../../../../src/components/songs/headers/SongInfoHeader';

describe('SongInfoHeader', () => {
  const baseSong = { songId: 's1', title: 'TestSong', artist: 'TestArtist', year: 2024, albumArt: 'https://example.com/art.jpg' };

  it('renders expanded with all song data', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} />
      </TestProviders>,
    );
    expect(container.textContent).toContain('TestSong');
    expect(container.textContent).toContain('TestArtist');
    expect(container.textContent).toContain('2024');
  });

  it('renders collapsed with smaller sizing', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={true} />
      </TestProviders>,
    );
    const img = container.querySelector('[class*="headerArt"]') as HTMLImageElement;
    expect(img?.style.width).toBe('80px');
  });

  it('renders expanded with large sizing', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} />
      </TestProviders>,
    );
    const img = container.querySelector('[class*="headerArt"]') as HTMLImageElement;
    expect(img?.style.width).toBe('120px');
  });

  it('shows songId when song is undefined', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={undefined} songId="fallback-id" collapsed={false} />
      </TestProviders>,
    );
    expect(container.textContent).toContain('fallback-id');
  });

  it('shows unknownArtist when song has no artist', () => {
    const noArtist = { ...baseSong, artist: undefined };
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={noArtist as any} songId="s1" collapsed={false} />
      </TestProviders>,
    );
    expect(container.textContent).toBeTruthy();
  });

  it('hides year when song has no year', () => {
    const noYear = { ...baseSong, year: undefined };
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={noYear as any} songId="s1" collapsed={false} />
      </TestProviders>,
    );
    expect(container.textContent).not.toContain('·');
  });

  it('shows placeholder when no albumArt', () => {
    const noArt = { ...baseSong, albumArt: undefined };
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={noArt as any} songId="s1" collapsed={false} />
      </TestProviders>,
    );
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('[class*="artPlaceholder"]')).toBeTruthy();
  });

  it('shows instrument icon and label', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} instrument={'Solo_Guitar' as any} />
      </TestProviders>,
    );
    expect(container.querySelector('[class*="headerRight"]')).toBeTruthy();
  });

  it('shows actions slot', () => {
    render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} actions={<button>Act</button>} />
      </TestProviders>,
    );
    expect(screen.getByText('Act')).toBeTruthy();
  });

  it('hides headerRight when no instrument or actions', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} />
      </TestProviders>,
    );
    expect(container.querySelector('[class*="headerRight"]')).toBeNull();
  });

  it('uses animate transitions', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={true} animate />
      </TestProviders>,
    );
    const img = container.querySelector('[class*="headerArt"]') as HTMLImageElement;
    expect(img?.style.transition).toBeTruthy();
  });

  it('collapsed instrument scale', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={true} instrument={'Solo_Guitar' as any} animate />
      </TestProviders>,
    );
    const iconWrap = container.querySelector('[class*="instIconWrap"]');
    expect(iconWrap).toBeTruthy();
  });
});
