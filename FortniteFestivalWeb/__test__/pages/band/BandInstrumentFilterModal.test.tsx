import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import BandInstrumentFilterModal, { type BandInstrumentFilterAssignment } from '../../../src/pages/band/modals/BandInstrumentFilterModal';
import { TestProviders } from '../../helpers/TestProviders';
import { stubElementDimensions, stubResizeObserver, stubScrollTo } from '../../helpers/browserStubs';
import type { SelectedBandProfile } from '../../../src/hooks/data/useSelectedProfile';

const apiMock = vi.hoisted(() => ({
  getBandDetail: vi.fn(),
}));

vi.mock('../../../src/api/client', () => ({
  api: apiMock,
}));

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
});

beforeEach(() => {
  apiMock.getBandDetail.mockReset();
  apiMock.getBandDetail.mockResolvedValue(makeBandDetail());
});

const selectedBand: SelectedBandProfile = {
  type: 'band',
  bandId: 'band-duo',
  bandType: 'Band_Duets',
  teamKey: 'acct-a:acct-b',
  displayName: 'Alpha + Bravo',
  members: [
    { accountId: 'acct-a', displayName: 'Alpha' },
    { accountId: 'acct-b', displayName: 'Bravo' },
  ],
};

function renderModal(overrides: Partial<{
  appliedAssignments: BandInstrumentFilterAssignment[];
  onApply: (assignments: BandInstrumentFilterAssignment[]) => void;
  onReset: () => void;
}> = {}) {
  const props = {
    visible: true,
    selectedBand,
    appliedAssignments: [] as BandInstrumentFilterAssignment[],
    onCancel: vi.fn(),
    onApply: vi.fn(),
    onReset: vi.fn(),
    ...overrides,
  };
  return {
    ...render(
      <TestProviders>
        <BandInstrumentFilterModal {...props} />
      </TestProviders>,
    ),
    props,
  };
}

describe('BandInstrumentFilterModal', () => {
  it('renders one selector row per bandmate with no initial selection', async () => {
    renderModal();

    expect(await screen.findByText('Bandmate #1')).toBeInTheDocument();
    expect(screen.getByText('Bandmate #2')).toBeInTheDocument();
    expect(screen.getByText('Alpha')).toBeInTheDocument();
    expect(screen.getByText('Bravo')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
  });

  it('hides instruments that were not played by a bandmate', async () => {
    renderModal();

    expect(await screen.findAllByTitle('Lead')).toHaveLength(2);
    expect(await screen.findAllByTitle('Bass')).toHaveLength(2);
    expect(screen.queryByTitle('Drums')).not.toBeInTheDocument();
  });

  it('applies only a complete exact configuration', async () => {
    const onApply = vi.fn();
    renderModal({ onApply });

    const leadButtons = await screen.findAllByTitle('Lead');
    const bassButtons = await screen.findAllByTitle('Bass');
    fireEvent.click(leadButtons[0]!);
    fireEvent.click(bassButtons[1]!);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Apply' })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));

    expect(onApply).toHaveBeenCalledWith([
      { accountId: 'acct-a', instrument: 'Solo_Guitar' },
      { accountId: 'acct-b', instrument: 'Solo_Bass' },
    ]);
  });

  it('confirms a selection that cannot match the current partial assignment', async () => {
    renderModal();

    const leadButtons = await screen.findAllByTitle('Lead');
    fireEvent.click(leadButtons[0]!);
    expect(leadButtons[1]).toHaveAttribute('data-conflict', 'true');
    fireEvent.click(leadButtons[1]!);

    expect(await screen.findByText('Invalid Configuration')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Keep Selection' }));
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
  });
});

function makeBandDetail() {
  return {
    band: {
      bandId: 'band-duo',
      teamKey: 'acct-a:acct-b',
      bandType: 'Band_Duets',
      members: [
        { accountId: 'acct-a', displayName: 'Alpha', instruments: ['Solo_Guitar', 'Solo_Bass'] },
        { accountId: 'acct-b', displayName: 'Bravo', instruments: ['Solo_Guitar', 'Solo_Bass'] },
      ],
    },
    ranking: null,
    configurations: [
      {
        rawInstrumentCombo: '0:1',
        comboId: 'Solo_Guitar+Solo_Bass',
        instruments: ['Solo_Guitar', 'Solo_Bass'],
        assignmentKey: 'acct-a=Solo_Guitar|acct-b=Solo_Bass',
        appearanceCount: 1,
        memberInstruments: {
          'acct-a': 'Solo_Guitar',
          'acct-b': 'Solo_Bass',
        },
      },
      {
        rawInstrumentCombo: '0:1',
        comboId: 'Solo_Guitar+Solo_Bass',
        instruments: ['Solo_Guitar', 'Solo_Bass'],
        assignmentKey: 'acct-a=Solo_Bass|acct-b=Solo_Guitar',
        appearanceCount: 1,
        memberInstruments: {
          'acct-a': 'Solo_Bass',
          'acct-b': 'Solo_Guitar',
        },
      },
    ],
  };
}
