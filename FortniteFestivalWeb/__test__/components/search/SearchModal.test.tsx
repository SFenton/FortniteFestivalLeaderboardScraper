import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act, waitFor } from '@testing-library/react';
import { useEffect } from 'react';
import SearchModal from '../../../src/components/search/SearchModal';
import { TestProviders } from '../../helpers/TestProviders';

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal() as Record<string, unknown>;
  return { ...actual, useNavigate: () => mockNavigate };
});

const mockApi = vi.hoisted(() => ({
  getSongs: vi.fn(),
  getShop: vi.fn(),
  searchAccounts: vi.fn(),
  searchBands: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

vi.mock('../../../src/components/modals/components/ModalShell', () => ({
  default: ({ visible, title, children, onOpenComplete, onCloseComplete }: {
    visible: boolean; title: string; children: React.ReactNode;
    onOpenComplete?: () => void; onCloseComplete?: () => void;
  }) => {
    useEffect(() => {
      if (visible) onOpenComplete?.();
      else onCloseComplete?.();
    }, [visible, onOpenComplete, onCloseComplete]);
    if (!visible) return null;
    return <div role="dialog" aria-label={title}><h2>{title}</h2>{children}</div>;
  },
}));

function setViewportQueries({ mobile = false }: { mobile?: boolean } = {}) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: query.includes('max-width') ? mobile : false,
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
}

function renderModal(overrides: Partial<React.ComponentProps<typeof SearchModal>> = {}) {
  const props = {
    visible: true,
    onClose: vi.fn(),
    defaultTarget: 'songs' as const,
    ...overrides,
  };
  const result = render(
    <TestProviders>
      <SearchModal {...props} />
    </TestProviders>,
  );
  return { ...result, props };
}

async function advanceAndFlush(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
  await act(async () => { await Promise.resolve(); });
}

const SEARCH_SETTLE_MS = 900;

function findAnimatedAncestor(text: string): HTMLElement | null {
  let el = screen.getByText(text) as HTMLElement | null;
  while (el && el !== document.body) {
    if (el.style.animation.includes('fadeInUp')) return el;
    el = el.parentElement;
  }
  return null;
}

function expectResultListEdgePadding(result: HTMLElement | null) {
  const resultList = result?.parentElement;
  expect(resultList?.style.paddingTop).toBe('40px');
  expect(resultList?.style.paddingBottom).toBe('40px');
  expect(resultList?.style.flexShrink).toBe('0');
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.clearAllMocks();
  mockNavigate.mockClear();
  setViewportQueries();
  mockApi.getSongs.mockResolvedValue({
    count: 2,
    currentSeason: 5,
    songs: [
      { songId: 'song-1', title: 'Butter Barn Hoedown', artist: 'Epic Games' },
      { songId: 'song-2', title: "(Don't Fear) The Reaper", artist: 'Blue Öyster Cult' },
    ],
  });
  mockApi.getShop.mockResolvedValue({ songs: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [{ accountId: 'p1', displayName: 'PlayerOne' }] });
  mockApi.searchBands.mockResolvedValue({
    query: 'but', normalizedQuery: 'but', rankBy: 'appearance', page: 1, pageSize: 10, totalCount: 1,
    isAmbiguous: false, needsDisambiguation: false, interpretations: [],
    results: [{
      bandId: 'band-1',
      teamKey: 'p1,p2',
      bandType: 'Band_Duets',
      appearanceCount: 3,
      members: [
        { accountId: 'p1', displayName: 'PlayerOne', instruments: [] },
        { accountId: 'p2', displayName: 'PlayerTwo', instruments: [] },
      ],
      ranking: null,
      matchedInterpretationIds: [],
      matchedAccountIds: ['p1'],
    }],
  });
});

afterEach(() => {
  vi.runOnlyPendingTimers();
  vi.useRealTimers();
});

describe('SearchModal', () => {
  it('opens on the configured default target', () => {
    renderModal({ defaultTarget: 'bands' });
    expect(screen.getByRole('dialog', { name: 'Search' })).toBeDefined();
    expect(screen.getByRole('tab', { name: 'Bands' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'Songs' })).toHaveAttribute('aria-selected', 'false');
  });

  it('does not show a separator above mobile target buttons', () => {
    setViewportQueries({ mobile: true });
    renderModal();
    const tablist = screen.getByRole('tablist');
    expect(tablist.getAttribute('style') ?? '').not.toContain('border-top');
  });

  it('lays out the result panel so loading spinners can center vertically', () => {
    renderModal();
    const tabpanel = screen.getByRole('tabpanel');
    expect(tabpanel.style.display).toBe('flex');
    expect(tabpanel.style.flexDirection).toBe('column');
  });

  it('runs song, player, and band searches for the same query', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => {
      expect(mockApi.searchAccounts).toHaveBeenCalledWith('but', 10);
      expect(mockApi.searchBands).toHaveBeenCalledWith({ q: 'but', page: 1, pageSize: 10 });
      expect(screen.getByText('Butter Barn Hoedown')).toBeDefined();
    });

    fireEvent.click(screen.getByRole('tab', { name: 'Players' }));
    expect(await screen.findByText('PlayerOne')).toBeDefined();
    expect(screen.queryByText('Player')).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: 'Bands' }));
    expect(await screen.findByText('PlayerOne')).toBeDefined();
    expect(await screen.findByText('PlayerTwo')).toBeDefined();
    expect(await screen.findByText('3')).toBeDefined();
  });

  it('matches songs the same way as the Songs page search', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: "Don't Fear" } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    expect(await screen.findByText("(Don't Fear) The Reaper")).toBeDefined();
    expect(screen.queryByText('FC')).toBeNull();
  });

  it('pads the result list so scroll fades do not cut into edge cards', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    expectResultListEdgePadding(findAnimatedAncestor('Butter Barn Hoedown'));
  });

  it('keeps player and band lists padded through the bottom scroll fade', async () => {
    renderModal();
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    fireEvent.click(screen.getByRole('tab', { name: 'Players' }));
    await waitFor(() => expect(findAnimatedAncestor('PlayerOne')).not.toBeNull());
    const playerResult = findAnimatedAncestor('PlayerOne');
    expect(playerResult?.style.flexShrink).toBe('0');
    expectResultListEdgePadding(playerResult);

    fireEvent.click(screen.getByRole('tab', { name: 'Bands' }));
    await waitFor(() => expect(findAnimatedAncestor('PlayerTwo')).not.toBeNull());
    const bandResult = findAnimatedAncestor('PlayerTwo');
    expect(bandResult?.style.flexShrink).toBe('0');
    expectResultListEdgePadding(bandResult);
  });

  it('staggers each target once per query when the target is viewed', async () => {
    renderModal();
    const input = screen.getByPlaceholderText('Search songs, players, or bands…');
    fireEvent.change(input, { target: { value: 'but' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    expect(findAnimatedAncestor('Butter Barn Hoedown')).not.toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: 'Players' }));
    await waitFor(() => expect(findAnimatedAncestor('PlayerOne')).not.toBeNull());

    fireEvent.click(screen.getByRole('tab', { name: 'Songs' }));
    await waitFor(() => expect(screen.getByText('Butter Barn Hoedown')).toBeDefined());
    expect(findAnimatedAncestor('Butter Barn Hoedown')).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: 'Players' }));
    await waitFor(() => expect(screen.getByText('PlayerOne')).toBeDefined());
    expect(findAnimatedAncestor('PlayerOne')).toBeNull();

    fireEvent.change(input, { target: { value: 'fear' } });
    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => expect(findAnimatedAncestor('PlayerOne')).not.toBeNull());
  });

  it('shows player results while band search is still pending', async () => {
    let resolveBands: (value: unknown) => void = () => {};
    mockApi.searchBands.mockReturnValue(new Promise(resolve => { resolveBands = resolve; }));

    renderModal({ defaultTarget: 'players' });
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'pla' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);

    await waitFor(() => {
      expect(screen.getByText('PlayerOne')).toBeDefined();
    });

    await act(async () => {
      resolveBands({
        query: 'pla', normalizedQuery: 'pla', rankBy: 'appearance', page: 1, pageSize: 10, totalCount: 0,
        isAmbiguous: false, needsDisambiguation: false, interpretations: [], results: [],
      });
      await Promise.resolve();
    });
  });

  it('navigates and closes when a player result is selected', async () => {
    const { props } = renderModal({ defaultTarget: 'players' });
    fireEvent.change(screen.getByPlaceholderText('Search songs, players, or bands…'), { target: { value: 'pla' } });

    await advanceAndFlush(SEARCH_SETTLE_MS);
    fireEvent.click(await screen.findByText('PlayerOne'));

    expect(props.onClose).toHaveBeenCalled();
    expect(mockNavigate).toHaveBeenCalledWith('/player/p1');
  });
});