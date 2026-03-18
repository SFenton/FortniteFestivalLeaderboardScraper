import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
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
    IoCheckmarkCircle: Stub, IoAlertCircle: Stub, IoSwapVerticalSharp: Stub,
    IoSparkles: Stub, IoStatsChart: Stub, IoSettings: Stub, IoFlash: Stub,
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

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
});

import SongDetailHeader from '../../../../src/pages/songinfo/components/SongDetailHeader';

describe('SongDetailHeader', () => {
  const baseSong = { songId: 's1', title: 'Test Song', artist: 'Artist', year: 2024, albumArt: 'art.jpg' } as any;

  it('renders unknown with no song', () => {
    render(
      <TestProviders>
        <SongDetailHeader song={undefined} songId="abc" collapsed={false} onOpenPaths={vi.fn()} />
      </TestProviders>,
    );
    expect(document.body.textContent).toBeTruthy();
  });

  it('renders with noTransition=true and collapsed=true', () => {
    const { container } = render(
      <MemoryRouter>
        <SongDetailHeader song={baseSong} songId="s1" collapsed noTransition onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    const header = container.firstElementChild as HTMLElement;
    expect(header).toBeTruthy();
  });

  it('renders with noTransition=false and collapsed=false', () => {
    const { container } = render(
      <MemoryRouter>
        <SongDetailHeader song={baseSong} songId="s1" collapsed={false} noTransition={false} onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    const header = container.firstElementChild as HTMLElement;
    expect(header).toBeTruthy();
  });

  it('renders without song (placeholder art)', () => {
    render(
      <MemoryRouter>
        <SongDetailHeader song={undefined} songId="s1" collapsed={false} onOpenPaths={vi.fn()} />
      </MemoryRouter>,
    );
    expect(screen.getByText('s1')).toBeTruthy();
  });
});
