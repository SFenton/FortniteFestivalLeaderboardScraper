import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { ReactNode } from 'react';
import type { PlayerBandsResponse } from '@festival/core/api/serverTypes';
import { Layout } from '@festival/theme';
import { buildPlayerBandsItems } from '../../../../src/pages/player/components/PlayerBandsSection';

vi.mock('../../../../src/pages/player/sections/PlayerSectionHeading', () => ({
  default: ({ title, actionLabel, actionTo, actionTestId }: { title: string; actionLabel?: string; actionTo?: string; actionTestId?: string }) => (
    <div data-testid="bands-section-heading">
      <span>{title}</span>
      {actionLabel && actionTo && <a data-testid={actionTestId} href={actionTo}>{actionLabel}<span aria-hidden="true">›</span></a>}
    </div>
  ),
}));

vi.mock('../../../../src/components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument, size }: { instrument: string; size?: number }) => <span data-testid={`icon-${instrument}`} data-size={size}>{instrument}</span>,
  getInstrumentStatusVisual: () => ({ fill: '#000', stroke: '#000' }),
}));

const t = (key: string, opts?: Record<string, unknown>) => {
  switch (key) {
    case 'player.bands':
      return `${opts?.name as string}'s Bands`;
    case 'player.allBands':
      return 'All Bands';
    case 'player.duos':
      return 'Duos';
    case 'player.trios':
      return 'Trios';
    case 'player.quads':
      return 'Quads';
    case 'player.viewAllBands':
      return `View all bands (${opts?.count as string})`;
    case 'common.viewAll':
      return 'View All';
    case 'player.noBandsYet':
      return 'No bands yet';
    case 'player.noBandsYetSubtitle':
      return 'Play with more teammates to populate this section.';
    default:
      return key;
  }
};

function makeBands(): PlayerBandsResponse {
  return {
    all: {
      totalCount: 6,
      entries: [{
        bandId: 'band-trio-123',
        teamKey: 'p1:p2:p3',
        bandType: 'Band_Trios',
        appearanceCount: 1,
        members: [
          { accountId: 'p1', displayName: 'TestPlayer', instruments: ['Solo_Guitar'] },
          { accountId: 'p2', displayName: 'BandMate', instruments: ['Solo_Bass'] },
          { accountId: 'p3', displayName: 'Singer', instruments: ['Solo_Vocals', 'Solo_Guitar'] },
        ],
      }],
    },
    duos: {
      totalCount: 3,
      entries: [{
        bandId: 'band-duo-123',
        teamKey: 'p1:p2',
        bandType: 'Band_Duets',
        appearanceCount: 2,
        members: [
          { accountId: 'p1', displayName: 'TestPlayer', instruments: ['Solo_Guitar'] },
          { accountId: 'p2', displayName: 'BandMate', instruments: ['Solo_Bass'] },
        ],
      }],
    },
    trios: { totalCount: 0, entries: [] },
    quads: { totalCount: 0, entries: [] },
  };
}

function renderNode(node: ReactNode) {
  return render(<MemoryRouter>{node}</MemoryRouter>);
}

describe('buildPlayerBandsItems', () => {
  it('returns section heading, filtered group headers, band cards, standalone view-all cards, and empty states', () => {
    const items = buildPlayerBandsItems(t, 'TestPlayer', makeBands());

    expect(items[0]?.key).toBe('bands-heading');
    expect(items[0]?.span).toBe(true);

    expect(items.find(item => item.key === 'bands-header-all')).toBeUndefined();
    expect(items.find(item => item.key === 'bands-entry-all-p1:p2:p3-0')).toBeUndefined();
    expect(items.find(item => item.key === 'bands-view-all-all')).toBeUndefined();

    expect(items.find(item => item.key === 'bands-header-duos')?.span).toBe(true);
    expect(items.find(item => item.key === 'bands-entry-duos-p1:p2-0')?.span).toBe(false);
    expect(items.find(item => item.key === 'bands-view-all-duos')?.span).toBe(true);
    expect(items.find(item => item.key === 'bands-view-all-duos')?.heightEstimate).toBe(Layout.entryRowHeight);

    expect(items.find(item => item.key === 'bands-empty-trios')?.span).toBe(true);
    expect(items.find(item => item.key === 'bands-empty-quads')?.span).toBe(true);
  });

  it('renders one member row per band member with name left and instrument icons on the right', () => {
    const items = buildPlayerBandsItems(t, 'TestPlayer', makeBands(), 'p1');
    const bandCard = items.find(item => item.key === 'bands-entry-duos-p1:p2-0');

    renderNode(bandCard?.node);

    expect(screen.getByText('TestPlayer')).toBeTruthy();
    expect(screen.getByText('BandMate')).toBeTruthy();
    expect(screen.queryByText('Singer')).toBeNull();
    expect(screen.getAllByTestId('icon-Solo_Guitar')).toHaveLength(1);
    expect(screen.getByTestId('icon-Solo_Bass')).toBeTruthy();
    expect(screen.getAllByTestId('icon-Solo_Guitar')[0]).toHaveAttribute('data-size', '32');
    expect(screen.getByTestId('icon-Solo_Bass')).toHaveAttribute('data-size', '32');
  });

  it('links band cards directly when bandId is present', () => {
    const items = buildPlayerBandsItems(t, 'TestPlayer', makeBands(), 'p1');
    const bandCard = items.find(item => item.key === 'bands-entry-duos-p1:p2-0');

    renderNode(bandCard?.node);

    expect(screen.getByTestId('player-bands-entry-duos-0')).toHaveAttribute('href', '/bands/band-duo-123?accountId=p1&bandType=Band_Duets&teamKey=p1%3Ap2&names=TestPlayer%20%2B%20BandMate');
  });

  it('uses lookup links for older stats payloads without bandId', () => {
    const bands = makeBands();
    bands.duos.entries[0] = { ...bands.duos.entries[0], bandId: undefined };
    const items = buildPlayerBandsItems(t, 'TestPlayer', bands, 'p1');
    const bandCard = items.find(item => item.key === 'bands-entry-duos-p1:p2-0');

    renderNode(bandCard?.node);

    expect(screen.getByTestId('player-bands-entry-duos-0')).toHaveAttribute('href', '/bands?accountId=p1&bandType=Band_Duets&teamKey=p1%3Ap2&names=TestPlayer%20%2B%20BandMate');
  });

  it('falls back to account id prefixes when building friendly names', () => {
    const bands = makeBands();
    bands.duos.entries[0] = {
      ...bands.duos.entries[0],
      members: [
        { accountId: 'p1abcdefghi', displayName: 'TestPlayer', instruments: ['Solo_Guitar'] },
        { accountId: 'p2abcdefghi', displayName: '', instruments: ['Solo_Bass'] },
      ],
    };
    const items = buildPlayerBandsItems(t, 'TestPlayer', bands, 'p1');
    const bandCard = items.find(item => item.key === 'bands-entry-duos-p1:p2-0');

    renderNode(bandCard?.node);

    expect(screen.getByTestId('player-bands-entry-duos-0')).toHaveAttribute('href', '/bands/band-duo-123?accountId=p1&bandType=Band_Duets&teamKey=p1%3Ap2&names=TestPlayer%20%2B%20p2abcdef');
  });

  it('renders standalone view-all cards and full-width empty states', () => {
    const items = buildPlayerBandsItems(t, 'TestPlayer', makeBands());
    const duosViewAllCard = items.find(item => item.key === 'bands-view-all-duos');
    const emptyState = items.find(item => item.key === 'bands-empty-trios');

    renderNode(
      <>
        {duosViewAllCard?.node}
        {emptyState?.node}
      </>,
    );

    expect(screen.getByText('View all bands (3)')).toBeTruthy();
    expect(screen.getByText('No bands yet')).toBeTruthy();
    expect(screen.getByText('Play with more teammates to populate this section.')).toBeTruthy();
  });

  it('links the heading view-all action to the selected account all bands page', () => {
    const items = buildPlayerBandsItems(t, 'TestPlayer', makeBands(), 'p1');
    const heading = items.find(item => item.key === 'bands-heading');

    renderNode(heading?.node);

    expect(screen.getByTestId('player-bands-view-all')).toHaveAttribute('href', '/bands/player/p1?group=all&page=1&name=TestPlayer');
    expect(screen.getByTestId('player-bands-view-all')).toHaveTextContent('View All');
  });

  it('omits the heading view-all action when no source account is available', () => {
    const items = buildPlayerBandsItems(t, 'TestPlayer', makeBands());
    const heading = items.find(item => item.key === 'bands-heading');

    renderNode(heading?.node);

    expect(screen.queryByTestId('player-bands-view-all')).toBeNull();
  });

  it('links group view-all cards to the selected account bands page for that group', () => {
    const items = buildPlayerBandsItems(t, 'TestPlayer', makeBands(), 'p1');
    const duosViewAllCard = items.find(item => item.key === 'bands-view-all-duos');

    renderNode(duosViewAllCard?.node);

    expect(screen.getByText('View all bands (3)').closest('a')).toHaveAttribute('href', '/bands/player/p1?group=duos&page=1&name=TestPlayer');
  });
});