import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { Route, Routes, useLocation } from 'react-router-dom';
import { TestProviders } from '../../helpers/TestProviders';
import { stubElementDimensions, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';

const mockApi = vi.hoisted(() => ({
  getPlayerBandsList: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
});

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.clearAllMocks();
  mockApi.getPlayerBandsList.mockImplementation((_accountId: string, group: string, page: number) => Promise.resolve({
    accountId: 'p1',
    group,
    totalCount: page === 1 ? 26 : 26,
    entries: page === 1 ? [makeBand('band-1', 'p1:p2', 'Band_Duets', 4)] : [makeBand('band-2', 'p1:p3', 'Band_Duets', 1)],
  }));
});

afterEach(() => {
  vi.useRealTimers();
});

const { default: PlayerBandsPage } = await import('../../../src/pages/band/PlayerBandsPage');

function makeBand(bandId: string, teamKey: string, bandType: 'Band_Duets' | 'Band_Trios' | 'Band_Quad', appearanceCount: number) {
  return {
    bandId,
    teamKey,
    bandType,
    appearanceCount,
    members: [
      { accountId: 'p1', displayName: 'Alpha', instruments: ['Solo_Guitar'] },
      { accountId: teamKey.endsWith('p3') ? 'p3' : 'p2', displayName: teamKey.endsWith('p3') ? 'Gamma' : 'Beta', instruments: ['Solo_Bass'] },
    ],
  };
}

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="current-location">{`${location.pathname}${location.search}`}</div>;
}

function renderPlayerBandsPage(route = '/bands/player/p1?group=all&page=1&name=Alpha') {
  return render(
    <TestProviders route={route}>
      <LocationProbe />
      <Routes>
        <Route path="/bands/player/:accountId" element={<PlayerBandsPage />} />
      </Routes>
    </TestProviders>,
  );
}

async function advancePastSpinner() {
  await act(async () => { await Promise.resolve(); });
  await act(async () => { await vi.advanceTimersByTimeAsync(600); });
  await act(async () => { await Promise.resolve(); });
  await act(async () => { await vi.advanceTimersByTimeAsync(600); });
  await act(async () => { await Promise.resolve(); });
}

describe('PlayerBandsPage', () => {
  it('renders a paged player bands list and links rows to band detail context', async () => {
    renderPlayerBandsPage();
    await advancePastSpinner();

    expect(mockApi.getPlayerBandsList).toHaveBeenCalledWith('p1', 'all', 1, 25);
    expect(await screen.findByText("Alpha's Bands")).toBeTruthy();
    expect(screen.getByText('Alpha')).toBeTruthy();
    expect(screen.getByText('Beta')).toBeTruthy();
    expect(screen.getByText('4')).toBeTruthy();
    expect(screen.getByText('appearances')).toBeTruthy();
    expect(screen.getByLabelText('View band Alpha + Beta')).toHaveAttribute(
      'href',
      '/bands/band-1?accountId=p1&bandType=Band_Duets&teamKey=p1%3Ap2&names=Alpha%20%2B%20Beta',
    );
  });

  it('limits the visible list to 25 bands when the service returns an unpaginated payload', async () => {
    mockApi.getPlayerBandsList.mockResolvedValue({
      accountId: 'p1',
      group: 'all',
      totalCount: 26,
      entries: Array.from({ length: 26 }, (_, index) => makeBand(`band-${index}`, `p1:p${index + 2}`, 'Band_Duets', 26 - index)),
    });

    renderPlayerBandsPage();
    await advancePastSpinner();

    expect(screen.getAllByRole('link', { name: /View band/ })).toHaveLength(25);
    expect(screen.getByText('1 / 2')).toBeTruthy();
  });

  it('applies the desktop filter modal by updating group and resetting to page one', async () => {
    renderPlayerBandsPage('/bands/player/p1?group=duos&page=2&name=Alpha');
    await advancePastSpinner();

    fireEvent.click(screen.getByRole('button', { name: 'Filter Bands' }));
    fireEvent.click(screen.getByRole('button', { name: /Trios/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));

    expect(screen.getByTestId('current-location')).toHaveTextContent('/bands/player/p1?group=trios&page=1&name=Alpha');
  });

  it('updates the page query when pagination advances', async () => {
    renderPlayerBandsPage();
    await advancePastSpinner();

    fireEvent.click(screen.getByLabelText('Next'));

    expect(screen.getByTestId('current-location')).toHaveTextContent('/bands/player/p1?group=all&page=2&name=Alpha');
  });

  it('shows an empty state for empty band groups', async () => {
    mockApi.getPlayerBandsList.mockResolvedValue({ accountId: 'p1', group: 'quads', totalCount: 0, entries: [] });

    renderPlayerBandsPage('/bands/player/p1?group=quads&page=1&name=Alpha');
    await advancePastSpinner();

    expect(screen.getByText('No bands found')).toBeTruthy();
    expect(screen.getByText('No Quads have been recorded for this player yet.')).toBeTruthy();
  });
});
