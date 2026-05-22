import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { TestProviders } from '../../../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubElementDimensions, stubIntersectionObserver, stubMatchMedia } from '../../../helpers/browserStubs';
import { Colors, IconSize, Layout } from '@festival/theme';
import anim from '../../../../src/styles/animations.module.css';

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
  const Stub = (p: any) => <span data-testid={p['aria-label'] ?? 'icon'} data-size={String(p.size ?? '')} />;
  return {
    IoMenu: Stub, IoClose: Stub, IoArrowUp: Stub, IoArrowDown: Stub,
    IoPerson: Stub, IoSearch: Stub, IoFilter: Stub, IoSwapVertical: Stub,
    IoMusicalNotes: Stub, IoChevronBack: Stub, IoEllipsisVertical: Stub,
    IoSettingsSharp: Stub, IoRefresh: Stub, IoAdd: Stub, IoRemove: Stub,
    IoCheckmarkCircle: Stub, IoAlertCircle: Stub, IoFlash: Stub, IoBagHandle: Stub,
  };
});

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
  stubIntersectionObserver();
  const originalCreateRange = document.createRange.bind(document);
  document.createRange = () => {
    const range = originalCreateRange();
    Object.assign(range, {
      getBoundingClientRect: () => ({
        top: 0,
        left: 0,
        bottom: 16,
        right: 120,
        width: 120,
        height: 16,
        x: 0,
        y: 0,
        toJSON() { return this; },
      }),
      getClientRects: () => [] as unknown as DOMRectList,
    });
    return range;
  };
  if (!HTMLElement.prototype.animate) {
    HTMLElement.prototype.animate = vi.fn().mockReturnValue({ cancel: vi.fn(), pause: vi.fn(), play: vi.fn(), finish: vi.fn(), onfinish: null, finished: Promise.resolve() }) as any;
  }
  if (!HTMLElement.prototype.getAnimations) {
    HTMLElement.prototype.getAnimations = vi.fn().mockReturnValue([]) as any;
  }
});

beforeEach(() => {
  stubMatchMedia(false);
});

import SongInfoHeader from '../../../../src/components/songs/headers/SongInfoHeader';

/** Find the album art img (has explicit width, unlike BackgroundImage's display:none probe). */
function findArtImg(container: HTMLElement): HTMLImageElement | null {
  return Array.from(container.querySelectorAll('img')).find(
    (i) => i.style.width,
  ) as HTMLImageElement ?? null;
}

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
    const img = findArtImg(container);
    expect(img?.style.width).toBe('80px');
  });

  it('renders expanded with large sizing', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} />
      </TestProviders>,
    );
    const img = findArtImg(container);
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
    expect(findArtImg(container)).toBeNull();
    // Art placeholder div has explicit size and backgroundColor
    const placeholder = Array.from(container.querySelectorAll('div')).find(
      (el) => el.style.backgroundColor && el.style.width === '120px',
    );
    expect(placeholder).toBeTruthy();
  });

  it('shows instrument icon and label', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} instrument={'Solo_Guitar' as any} />
      </TestProviders>,
    );
    expect(container.querySelector('img[alt="Solo_Guitar"]')).toBeTruthy();
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
    // All imgs have empty alt (album art + BackgroundImage probe) — no instrument icons
    expect(Array.from(container.querySelectorAll('img')).every((i) => i.alt === '')).toBe(true);
  });

  it('uses animate transitions', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={true} animate />
      </TestProviders>,
    );
    // animate mode uses CSS module classes for scroll-linked interpolation
    // instead of inline transitions — art img no longer has inline width/transition
    const imgs = Array.from(container.querySelectorAll('img[src="https://example.com/art.jpg"]')) as HTMLElement[];
    const artImg = imgs.find((i) => !i.style.display);
    expect(artImg).toBeTruthy();
    expect(artImg!.style.width).toBeFalsy(); // driven by CSS module, not inline
  });

  it('collapsed instrument scale', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={true} instrument={'Solo_Guitar' as any} animate />
      </TestProviders>,
    );
    const iconWrap = container.querySelector('img[alt="Solo_Guitar"]');
    expect(iconWrap).toBeTruthy();
  });

  it('calls onTitleClick when title area is clicked', () => {
    const handler = vi.fn();
    render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} onTitleClick={handler} />
      </TestProviders>,
    );
    const titleEl = screen.getByText('TestSong');
    // Click the role="link" ancestor wrapping the title
    titleEl.closest('[role="link"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('title area has cursor pointer when onTitleClick is provided', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} onTitleClick={() => {}} />
      </TestProviders>,
    );
    const linkEl = container.querySelector('[role="link"]') as HTMLElement;
    expect(linkEl).toBeTruthy();
    expect(linkEl.style.cursor).toBe('pointer');
  });

  it('title area is not clickable when onTitleClick is omitted', () => {
    const { container } = render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} />
      </TestProviders>,
    );
    expect(container.querySelector('[role="link"]')).toBeNull();
  });

  it('renders View Paths on desktop with compact pill metrics', () => {
    render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} onOpenPaths={vi.fn()} />
      </TestProviders>,
    );

    const button = screen.getByRole('button', { name: 'View Paths' });
    expect(button).toHaveStyle({
      height: `${Layout.pillButtonHeight}px`,
      fontSize: '12px',
      paddingLeft: '12px',
      paddingRight: '12px',
    });

    const icon = button.querySelector(`[data-size="${IconSize.action}"]`);
    expect(icon).not.toBeNull();
  });

  it('renders View Paths on mobile as a compact purple circle', () => {
    stubMatchMedia(true);

    render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={true} onOpenPaths={vi.fn()} />
      </TestProviders>,
    );

    const button = screen.getByRole('button', { name: 'View Paths' });
    const buttonStyle = button.getAttribute('style') ?? '';
    expect(buttonStyle).toContain(`width: ${Layout.pillButtonHeight}px`);
    expect(buttonStyle).toContain(`height: ${Layout.pillButtonHeight}px`);
    expect(buttonStyle).toContain('border-radius: 999px');

    const icon = button.querySelector(`[data-size="${IconSize.action}"]`);
    expect(icon).not.toBeNull();
  });

  it('renders Item Shop on mobile with compact path circle metrics', () => {
    stubMatchMedia(true);

    render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={true} shopUrl="https://example.com/shop/s1" />
      </TestProviders>,
    );

    const link = screen.getByRole('link', { name: 'Item Shop' });
    const linkStyle = link.getAttribute('style') ?? '';
    expect(linkStyle).toContain(`width: ${Layout.pillButtonHeight}px`);
    expect(linkStyle).toContain(`height: ${Layout.pillButtonHeight}px`);
    expect(linkStyle).toContain('border-radius: 999px');

    const icon = link.querySelector(`[data-size="${IconSize.sm}"]`);
    expect(icon).not.toBeNull();
  });

  it('renders pulsing Item Shop on mobile with neutral opaque glass backing', () => {
    stubMatchMedia(true);

    render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={true} shopUrl="https://example.com/shop/s1" shopPulse />
      </TestProviders>,
    );

    const link = screen.getByRole('link', { name: 'Item Shop' });
    const linkStyle = link.getAttribute('style') ?? '';
    expect(linkStyle).toContain(`width: ${Layout.pillButtonHeight}px`);
    expect(linkStyle).toContain(`height: ${Layout.pillButtonHeight}px`);
    expect(linkStyle).toContain('border-radius: 999px');
    expect(link.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(link.style.border).toBe('1px solid rgba(255, 255, 255, 0.08)');
  });

  it('uses gold Item Shop pulse for new shop songs', () => {
    render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} shopUrl="https://example.com/shop/s1" shopPulse shopNew />
      </TestProviders>,
    );

    const link = screen.getByRole('link', { name: 'Item Shop' });
    expect(link.className).toContain(anim.shopBreatheGold);
  });

  it('prioritizes leaving-tomorrow pulse over gold Item Shop pulse', () => {
    render(
      <TestProviders>
        <SongInfoHeader song={baseSong as any} songId="s1" collapsed={false} shopUrl="https://example.com/shop/s1" shopPulse shopNew shopLeavingTomorrow />
      </TestProviders>,
    );

    const link = screen.getByRole('link', { name: 'Item Shop' });
    expect(link.className).toContain(anim.shopBreatheRed);
    expect(link.className).not.toContain(anim.shopBreatheGold);
  });
});
