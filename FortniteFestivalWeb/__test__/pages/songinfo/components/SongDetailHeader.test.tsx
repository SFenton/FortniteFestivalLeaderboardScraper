/**
 * SEPARATE FILE: Cannot consolidate into SongDetailPage.test.tsx because this
 * file patches HTMLElement.prototype.animate/getAnimations and uses custom
 * module-level mocks (react-icons, api/client) that would conflict.
 */
import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { TestProviders } from '../../../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubElementDimensions, stubIntersectionObserver, stubMatchMedia } from '../../../helpers/browserStubs';
import { Colors, Layout } from '@festival/theme';

const shopStateMock = vi.hoisted(() => ({
  useShopState: vi.fn(),
}));

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
    IoBagHandle: Stub, IoFunnel: Stub, IoPersonAdd: Stub,
  };
});

vi.mock('../../../../src/hooks/data/useShopState', () => ({
  useShopState: shopStateMock.useShopState,
}));

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
  stubMatchMedia(false);
  shopStateMock.useShopState.mockReturnValue({
    isShopVisible: false,
    isShopHighlighted: () => false,
    isLeavingTomorrow: () => false,
    isShopNew: () => false,
    getShopUrl: () => undefined,
  });
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
      <TestProviders>
        <SongDetailHeader song={baseSong} songId="s1" collapsed noTransition onOpenPaths={vi.fn()} />
      </TestProviders>,
    );
    const header = container.firstElementChild as HTMLElement;
    expect(header).toBeTruthy();
  });

  it('renders with noTransition=false and collapsed=false', () => {
    const { container } = render(
      <TestProviders>
        <SongDetailHeader song={baseSong} songId="s1" collapsed={false} noTransition={false} onOpenPaths={vi.fn()} />
      </TestProviders>,
    );
    const header = container.firstElementChild as HTMLElement;
    expect(header).toBeTruthy();
  });

  it('renders without song (placeholder art)', () => {
    render(
      <TestProviders>
        <SongDetailHeader song={undefined} songId="s1" collapsed={false} onOpenPaths={vi.fn()} />
      </TestProviders>,
    );
    expect(screen.getByText('s1')).toBeTruthy();
  });

  it('renders mobile Item Shop as a compact opaque pill', () => {
    stubMatchMedia(true);
    shopStateMock.useShopState.mockReturnValue({
      isShopVisible: true,
      isShopHighlighted: () => false,
      isLeavingTomorrow: () => false,
      isShopNew: () => false,
      getShopUrl: () => 'https://example.com/shop/s1',
    });

    render(
      <TestProviders>
        <SongDetailHeader song={baseSong} songId="s1" collapsed onOpenPaths={vi.fn()} />
      </TestProviders>,
    );

    const link = screen.getByRole('link', { name: 'Item Shop' });
    const linkStyle = link.getAttribute('style') ?? '';
    expect(linkStyle).toContain(`min-width: ${Layout.pillButtonHeight}px`);
    expect(linkStyle).toContain('max-width: 112px');
    expect(linkStyle).toContain(`height: ${Layout.pillButtonHeight}px`);
    expect(linkStyle).toContain('border-radius: 999px');
    expect(link).toHaveStyle({ backgroundColor: Colors.statusGreenStroke });
    expect(screen.getByText('Item Shop')).toBeTruthy();
  });

  it('renders pulsing mobile Item Shop with neutral opaque glass backing', () => {
    stubMatchMedia(true);
    shopStateMock.useShopState.mockReturnValue({
      isShopVisible: true,
      isShopHighlighted: () => true,
      isLeavingTomorrow: () => false,
      isShopNew: () => false,
      getShopUrl: () => 'https://example.com/shop/s1',
    });

    render(
      <TestProviders>
        <SongDetailHeader song={baseSong} songId="s1" collapsed onOpenPaths={vi.fn()} />
      </TestProviders>,
    );

    const link = screen.getByRole('link', { name: 'Item Shop' });
    expect(link.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(link.style.border).toBe('1px solid rgba(255, 255, 255, 0.08)');
  });

  it('uses green as the static Item Shop availability color', () => {
    shopStateMock.useShopState.mockReturnValue({
      isShopVisible: true,
      isShopHighlighted: () => false,
      isLeavingTomorrow: () => false,
      isShopNew: () => false,
      getShopUrl: () => 'https://example.com/shop/s1',
    });

    render(
      <TestProviders>
        <SongDetailHeader song={baseSong} songId="s1" collapsed={false} onOpenPaths={vi.fn()} />
      </TestProviders>,
    );

    expect(screen.getByRole('link', { name: 'Item Shop' })).toHaveStyle({ backgroundColor: Colors.statusGreenStroke });
  });
});
