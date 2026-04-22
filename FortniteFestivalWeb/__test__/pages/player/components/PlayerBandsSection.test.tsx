import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { PlayerBandsResponse } from '@festival/core/api/serverTypes';
import { Layout } from '@festival/theme';
import { buildPlayerBandsItems } from '../../../../src/pages/player/components/PlayerBandsSection';

vi.mock('../../../../src/pages/player/sections/PlayerSectionHeading', () => ({
  default: ({ title }: { title: string }) => <div data-testid="bands-section-heading">{title}</div>,
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
        teamKey: 'p1:p2:p3',
        bandType: 'Band_Trios',
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
        teamKey: 'p1:p2',
        bandType: 'Band_Duets',
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

describe('buildPlayerBandsItems', () => {
  it('returns section heading, group headers, band cards, standalone view-all cards, and empty states', () => {
    const items = buildPlayerBandsItems(t, 'TestPlayer', makeBands());

    expect(items[0]?.key).toBe('bands-heading');
    expect(items[0]?.span).toBe(true);

    expect(items.find(item => item.key === 'bands-header-all')?.span).toBe(true);
    expect(items.find(item => item.key === 'bands-entry-all-p1:p2:p3-0')?.span).toBe(false);
    expect(items.find(item => item.key === 'bands-view-all-all')?.span).toBe(true);
    expect(items.find(item => item.key === 'bands-view-all-all')?.heightEstimate).toBe(Layout.entryRowHeight);

    expect(items.find(item => item.key === 'bands-header-duos')?.span).toBe(true);
    expect(items.find(item => item.key === 'bands-entry-duos-p1:p2-0')?.span).toBe(false);
    expect(items.find(item => item.key === 'bands-view-all-duos')?.span).toBe(true);

    expect(items.find(item => item.key === 'bands-empty-trios')?.span).toBe(true);
    expect(items.find(item => item.key === 'bands-empty-quads')?.span).toBe(true);
  });

  it('renders one member row per band member with name left and instrument icons on the right', () => {
    const items = buildPlayerBandsItems(t, 'TestPlayer', makeBands());
    const bandCard = items.find(item => item.key === 'bands-entry-all-p1:p2:p3-0');

    render(<>{bandCard?.node}</>);

    expect(screen.getByText('TestPlayer')).toBeTruthy();
    expect(screen.getByText('BandMate')).toBeTruthy();
    expect(screen.getByText('Singer')).toBeTruthy();
    expect(screen.getAllByTestId('icon-Solo_Guitar')).toHaveLength(2);
    expect(screen.getByTestId('icon-Solo_Bass')).toBeTruthy();
    expect(screen.getByTestId('icon-Solo_Vocals')).toBeTruthy();
    expect(screen.getAllByTestId('icon-Solo_Guitar')[0]).toHaveAttribute('data-size', '32');
    expect(screen.getByTestId('icon-Solo_Bass')).toHaveAttribute('data-size', '32');
    expect(screen.getByTestId('icon-Solo_Vocals')).toHaveAttribute('data-size', '32');
  });

  it('renders standalone view-all cards and full-width empty states', () => {
    const items = buildPlayerBandsItems(t, 'TestPlayer', makeBands());
    const allViewAllCard = items.find(item => item.key === 'bands-view-all-all');
    const emptyState = items.find(item => item.key === 'bands-empty-trios');

    render(
      <>
        {allViewAllCard?.node}
        {emptyState?.node}
      </>,
    );

    expect(screen.getByText('View all bands (6)')).toBeTruthy();
    expect(screen.getByText('No bands yet')).toBeTruthy();
    expect(screen.getByText('Play with more teammates to populate this section.')).toBeTruthy();
  });
});