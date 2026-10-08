import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import type { ShopSong } from '@festival/core/api';
import ShopPage from '../../../src/pages/shop/ShopPage';
import { TestProviders } from '../../helpers/TestProviders';
import { seedAllFirstRunSeen } from '../../helpers/firstRunState';
import { stubElementDimensions, stubMatchMedia, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';
import { SHOP_SORT_STORAGE_KEY } from '../../../src/pages/shop/shopSort';

const SHOP_SONGS = vi.hoisted((): ShopSong[] => [
  { songId: 'b', title: 'Bravo', artist: 'Zed', year: 2001, shopUrl: 'https://shop/b' },
  { songId: 'a', title: 'Alpha', artist: 'Mike', year: 1999, shopUrl: 'https://shop/a' },
  { songId: 'c', title: 'Charlie', artist: 'Alan', year: 2010, shopUrl: 'https://shop/c' },
]);

const CATALOG = vi.hoisted(() => [
  { songId: 'a', title: 'Alpha', artist: 'Mike', year: 1999, durationSeconds: 300 },
  { songId: 'b', title: 'Bravo', artist: 'Zed', year: 2001, durationSeconds: 120 },
  { songId: 'c', title: 'Charlie', artist: 'Alan', year: 2010, durationSeconds: 200 },
]);

vi.mock('../../../src/api/client', () => ({
  api: {
    getSongs: vi.fn().mockResolvedValue({ songs: CATALOG, count: CATALOG.length, currentSeason: 5 }),
    getIncomingSongs: vi.fn().mockResolvedValue({ songs: [] }),
    getShop: vi.fn().mockResolvedValue({ songs: SHOP_SONGS }),
    getVersion: vi.fn().mockResolvedValue({ version: '1.0.0' }),
  },
}));

vi.mock('../../../src/hooks/data/useShopState', () => ({
  useShopState: () => ({
    shopSongs: SHOP_SONGS,
    isLeavingTomorrow: () => false,
    isShopNew: () => false,
  }),
}));

vi.mock('../../../src/contexts/FestivalContext', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../src/contexts/FestivalContext')>();
  return { ...actual, useFestival: () => ({ state: { songs: CATALOG, isLoading: false, error: null } }) };
});

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1200, height: 800 });
  stubElementDimensions(800);
  stubMatchMedia(false);
});

function renderPage() {
  return render(<TestProviders route="/shop"><ShopPage /></TestProviders>);
}

function visibleTitles(): string[] {
  return ['Alpha', 'Bravo', 'Charlie']
    .map(title => ({ title, el: screen.getByText(title) }))
    .sort((x, y) => (x.el.compareDocumentPosition(y.el) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1))
    .map(x => x.title);
}

async function chooseSort(mode: string, descending = false) {
  fireEvent.click(screen.getByRole('button', { name: 'Sort' }));
  const dialog = await screen.findByRole('dialog');
  fireEvent.click(within(dialog).getByText(mode));
  if (descending) fireEvent.click(within(dialog).getByLabelText('Descending'));
  fireEvent.click(within(dialog).getByText('Apply Sort Changes'));
}

describe('ShopPage sorting', () => {
  beforeEach(() => {
    localStorage.clear();
    seedAllFirstRunSeen();
  });

  it('defaults to title A–Z in the grid', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText('Alpha')).toBeDefined());
    expect(visibleTitles()).toEqual(['Alpha', 'Bravo', 'Charlie']);
  });

  it('sorts the grid by duration (from the catalogue) and persists the choice', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText('Alpha')).toBeDefined());
    await chooseSort('Duration', true);
    await waitFor(() => expect(visibleTitles()).toEqual(['Alpha', 'Charlie', 'Bravo']));
    expect(JSON.parse(localStorage.getItem(SHOP_SORT_STORAGE_KEY)!)).toEqual({ sortMode: 'duration', sortAscending: false });
  });

  it('restores a saved sort and applies it to the list view', async () => {
    localStorage.setItem('fst:shopView', 'list');
    localStorage.setItem(SHOP_SORT_STORAGE_KEY, JSON.stringify({ sortMode: 'artist', sortAscending: true }));
    renderPage();
    await waitFor(() => expect(screen.getByText('Alpha')).toBeDefined());
    // List rows show artist, year and catalogue duration like the Songs list.
    expect(screen.getByText(/Alan · 2010 · 3:20/)).toBeDefined();
    expect(visibleTitles()).toEqual(['Charlie', 'Alpha', 'Bravo']);
  });

  it('sorts by year descending', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText('Alpha')).toBeDefined());
    await chooseSort('Year', true);
    await waitFor(() => expect(visibleTitles()).toEqual(['Charlie', 'Bravo', 'Alpha']));
  });
});
