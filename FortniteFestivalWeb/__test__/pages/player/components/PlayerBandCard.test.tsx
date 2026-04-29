import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { PlayerBandEntry } from '@festival/core/api/serverTypes';
import PlayerBandCard, {
  estimatePlayerBandCardHeight,
  resolveBandMemberInstrumentLayout,
} from '../../../../src/pages/player/components/PlayerBandCard';

const mockUseContainerWidth = vi.hoisted(() => vi.fn(() => 800));

vi.mock('../../../../src/hooks/ui/useContainerWidth', () => ({
  useContainerWidth: mockUseContainerWidth,
}));

vi.mock('../../../../src/components/display/InstrumentIcons', () => ({
  InstrumentIcon: ({ instrument, size }: { instrument: string; size?: number }) => (
    <span data-testid={`instrument-icon-${instrument}`} data-size={size}>{instrument}</span>
  ),
}));

const ALL_INSTRUMENTS = [
  'Solo_Guitar',
  'Solo_Bass',
  'Solo_Drums',
  'Solo_Vocals',
  'Solo_PeripheralGuitar',
  'Solo_PeripheralBass',
  'Solo_PeripheralVocals',
  'Solo_PeripheralCymbals',
  'Solo_PeripheralDrums',
] as const;

function makeEntry(instrumentCount: number, displayName = 'BandMate'): PlayerBandEntry {
  return {
    bandId: 'band-1',
    teamKey: 'p1:p2',
    bandType: 'Band_Duets',
    members: [{
      accountId: 'p2',
      displayName,
      instruments: ALL_INSTRUMENTS.slice(0, instrumentCount),
    }],
  } as PlayerBandEntry;
}

function makeMixedEntry(): PlayerBandEntry {
  return {
    bandId: 'band-1',
    teamKey: 'p1:p2',
    bandType: 'Band_Duets',
    members: [
      {
        accountId: 'p1',
        displayName: 'OneInst',
        instruments: ['Solo_Guitar'],
      },
      {
        accountId: 'p2',
        displayName: 'NineInst',
        instruments: ALL_INSTRUMENTS,
      },
    ],
  } as PlayerBandEntry;
}

function renderCard(entry: PlayerBandEntry) {
  return render(
    <MemoryRouter>
      <PlayerBandCard entry={entry} sourceAccountId="p1" testId="band-card" />
    </MemoryRouter>,
  );
}

function getInstrumentRowCounts(): number[] {
  return screen.getAllByTestId('band-member-instrument-row').map(row => within(row).getAllByTestId(/instrument-icon-/).length);
}

function getInstrumentRowCountsForMember(memberRow: HTMLElement): number[] {
  return within(memberRow).getAllByTestId('band-member-instrument-row').map(row => within(row).getAllByTestId(/instrument-icon-/).length);
}

describe('PlayerBandCard adaptive instrument layout', () => {
  beforeEach(() => {
    mockUseContainerWidth.mockReturnValue(800);
  });

  it('keeps instruments inline when the measured width is sufficient', () => {
    renderCard(makeEntry(9, 'BandMate'));

    expect(screen.getByTestId('band-member-row')).toHaveAttribute('data-layout', 'inline');
    expect(getInstrumentRowCounts()).toEqual([9]);
    expect(screen.getByTestId('band-member-instrument-row')).toHaveStyle({ justifyContent: 'flex-end' });
  });

  it('moves instruments below the username when measured width would squeeze the name', () => {
    mockUseContainerWidth.mockReturnValue(220);

    renderCard(makeEntry(4, 'captainparticles'));

    expect(screen.getByTestId('band-member-row')).toHaveAttribute('data-layout', 'stacked');
    expect(screen.getByTestId('band-member-instrument-rows')).toHaveStyle({
      alignItems: 'stretch',
      justifyContent: 'flex-start',
      paddingTop: '4px',
      paddingBottom: '4px',
    });
    expect(screen.getByTestId('band-member-instrument-row')).toHaveStyle({ justifyContent: 'flex-start' });
    expect(getInstrumentRowCounts()).toEqual([4]);
  });

  it('splits six instruments into two even rows when one stacked row is still too wide', () => {
    mockUseContainerWidth.mockReturnValue(180);

    renderCard(makeEntry(6, 'BandMate'));

    expect(screen.getByTestId('band-member-row')).toHaveAttribute('data-layout', 'stacked');
    expect(getInstrumentRowCounts()).toEqual([3, 3]);
  });

  it('splits nine instruments with the extra icon on the top row', () => {
    mockUseContainerWidth.mockReturnValue(260);

    renderCard(makeEntry(9, 'BandMate'));

    expect(screen.getByTestId('band-member-row')).toHaveAttribute('data-layout', 'stacked');
    expect(getInstrumentRowCounts()).toEqual([5, 4]);
  });

  it('uses card-wide stacked mode when any member row needs stacked layout', () => {
    mockUseContainerWidth.mockReturnValue(260);

    renderCard(makeMixedEntry());

    const memberRows = screen.getAllByTestId('band-member-row');
    expect(memberRows).toHaveLength(2);
    expect(memberRows[0]).toHaveAttribute('data-layout', 'stacked');
    expect(memberRows[1]).toHaveAttribute('data-layout', 'stacked');
    expect(getInstrumentRowCountsForMember(memberRows[0]!)).toEqual([1]);
    expect(getInstrumentRowCountsForMember(memberRows[1]!)).toEqual([5, 4]);
  });

  it('keeps all members inline when no row needs stacked layout', () => {
    mockUseContainerWidth.mockReturnValue(800);

    renderCard(makeMixedEntry());

    const memberRows = screen.getAllByTestId('band-member-row');
    expect(memberRows).toHaveLength(2);
    expect(memberRows[0]).toHaveAttribute('data-layout', 'inline');
    expect(memberRows[1]).toHaveAttribute('data-layout', 'inline');
  });

  it('uses larger username styling in card-wide stacked mode', () => {
    mockUseContainerWidth.mockReturnValue(260);

    renderCard(makeMixedEntry());

    const names = screen.getAllByTestId('band-member-name');
    expect(names[0]).toHaveStyle({ fontSize: '16px', fontWeight: '700', paddingTop: '8px' });
    expect(names[1]).toHaveStyle({ fontSize: '16px', fontWeight: '700', paddingTop: '8px' });
  });

  it('does not render an instrument row for members without instruments', () => {
    renderCard(makeEntry(0, 'BandMate'));

    expect(screen.getByTestId('band-member-row')).toHaveAttribute('data-layout', 'inline');
    expect(screen.queryByTestId('band-member-instrument-rows')).toBeNull();
  });

  it('deduplicates duplicate instruments before resolving rows', () => {
    mockUseContainerWidth.mockReturnValue(140);
    const entry = {
      ...makeEntry(0, 'BandMate'),
      members: [{
        accountId: 'p2',
        displayName: 'BandMate',
        instruments: ['Solo_Guitar', 'Solo_Guitar', 'Solo_Bass'],
      }],
    } as PlayerBandEntry;

    renderCard(entry);

    expect(getInstrumentRowCounts()).toEqual([2]);
    expect(screen.getAllByTestId(/instrument-icon-/)).toHaveLength(2);
  });

  it('can render score metadata in the footer instead of appearance count', () => {
    render(
      <MemoryRouter>
        <PlayerBandCard
          entry={makeMixedEntry()}
          rank={7}
          testId="band-card"
          scoreFooter={<span>Season 4 - 1,234,567 - 99%</span>}
          scoreFooterAriaLabel="Season 4 score 1,234,567 99 percent"
        />
      </MemoryRouter>,
    );

    expect(screen.getByLabelText('Season 4 score 1,234,567 99 percent')).toBeInTheDocument();
    expect(screen.getByLabelText('Season 4 score 1,234,567 99 percent')).toHaveStyle({ paddingLeft: '8px', paddingRight: '8px' });
    expect(screen.getByText('Season 4 - 1,234,567 - 99%')).toBeInTheDocument();
    expect(screen.getByTestId('band-rank-rail')).toHaveTextContent('#7');
    expect(screen.getByTestId('band-rank-rail')).toHaveStyle({ alignSelf: 'stretch' });
    expect(screen.getByTestId('band-ranked-card-content')).toContainElement(screen.getByTestId('band-rank-rail'));
    expect(screen.getByTestId('band-ranked-card-content')).toContainElement(screen.getByLabelText('Season 4 score 1,234,567 99 percent'));
    expect(screen.getByTestId('band-card-member-content')).not.toContainElement(screen.getByTestId('band-rank-rail'));
    expect(screen.queryByText('appearances')).toBeNull();
  });

  it('renders member metadata before member instrument icons', () => {
    render(
      <MemoryRouter>
        <PlayerBandCard
          entry={makeEntry(1, 'BandMate')}
          testId="band-card"
          renderMemberMetadata={() => <span data-testid="member-meta">Meta</span>}
        />
      </MemoryRouter>,
    );

    const trailing = screen.getByTestId('band-member-trailing');
    const metadata = within(trailing).getByTestId('member-meta');
    const instrument = within(trailing).getByTestId('instrument-icon-Solo_Guitar');
    expect(trailing).toContainElement(metadata);
    expect(trailing).toContainElement(instrument);
    expect(metadata.compareDocumentPosition(instrument) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('resolves layout from measured width without relying on viewport category', () => {
    expect(resolveBandMemberInstrumentLayout('BandMate', 9, 800)).toEqual({ stacked: false, instrumentRowCount: 1 });
    expect(resolveBandMemberInstrumentLayout('BandMate', 9, 260)).toEqual({ stacked: true, instrumentRowCount: 2 });
    expect(resolveBandMemberInstrumentLayout('captainparticles', 4, 220)).toEqual({ stacked: true, instrumentRowCount: 1 });
  });

  it('increases height estimates for wrapped instrument rows', () => {
    const entry = makeEntry(9, 'BandMate');
    const inlineEstimate = estimatePlayerBandCardHeight(entry, false, 800);
    const wrappedEstimate = estimatePlayerBandCardHeight(entry, false, 260);

    expect(inlineEstimate).toBe(64);
    expect(wrappedEstimate).toBeGreaterThan(inlineEstimate);
  });
});
