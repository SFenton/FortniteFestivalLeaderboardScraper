import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import BandInstrumentFilterModal, { type BandInstrumentFilterApplyPayload, type BandInstrumentFilterAssignment } from '../../../src/pages/band/modals/BandInstrumentFilterModal';
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

const appliedAssignments: BandInstrumentFilterAssignment[] = [
  { accountId: 'acct-a', instrument: 'Solo_Guitar' },
  { accountId: 'acct-b', instrument: 'Solo_Bass' },
];

function renderModal(overrides: Partial<{
  appliedAssignments: BandInstrumentFilterAssignment[];
  onApply: (payload: BandInstrumentFilterApplyPayload) => void;
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

function instrumentSelectionScale(title: string, index = 0) {
  return screen.getAllByTitle(title)[index]?.querySelector('div')?.style.transform;
}

describe('BandInstrumentFilterModal', () => {
  it('renders one selector section per instrument slot with no bandmate names', async () => {
    renderModal();

    const firstSlot = await screen.findByText('Instrument #1');
    expect(firstSlot).toBeInTheDocument();
    expect(screen.getByText('Select the first instrument played in your band.')).toBeInTheDocument();
    expect(screen.getByText('Instrument #2')).toBeInTheDocument();
    expect(screen.getByText('Select the second instrument played in your band.')).toBeInTheDocument();
    expect(screen.queryByText('Bandmate #1')).not.toBeInTheDocument();
    expect(screen.queryByText('Bandmate #2')).not.toBeInTheDocument();
    expect(screen.queryByText('Alpha')).not.toBeInTheDocument();
    expect(screen.queryByText('Bravo')).not.toBeInTheDocument();
    expect(firstSlot.parentElement?.style.getPropertyValue('--frosted-card')).toBe('');
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
  });

  it('renders reset title and hint copy above the reset button', async () => {
    renderModal();

    expect(await screen.findByText('Reset Instruments')).toBeInTheDocument();
    expect(screen.getByText('Clear the selected instrument combination.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reset' })).toBeInTheDocument();
  });

  it('shows the complete unique instrument set for every combo slot', async () => {
    renderModal();

    expect(await screen.findAllByTitle('Lead')).toHaveLength(2);
    expect(await screen.findAllByTitle('Bass')).toHaveLength(2);
    expect(await screen.findAllByTitle('Drums')).toHaveLength(2);
    expect(screen.queryByTitle('Vocals')).not.toBeInTheDocument();
  });

  it('applies a complete generic combo regardless of slot order', async () => {
    const onApply = vi.fn();
    renderModal({ onApply });

    const leadButtons = await screen.findAllByTitle('Lead');
    const bassButtons = await screen.findAllByTitle('Bass');
    fireEvent.click(bassButtons[0]!);
    fireEvent.click(leadButtons[1]!);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Apply' })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      comboId: 'Solo_Guitar+Solo_Bass',
      assignments: [
        { accountId: 'acct-a', instrument: 'Solo_Bass' },
        { accountId: 'acct-b', instrument: 'Solo_Guitar' },
      ],
      configurations: expect.arrayContaining([
        expect.objectContaining({ assignmentKey: 'acct-a=Solo_Guitar|acct-b=Solo_Bass' }),
        expect.objectContaining({ assignmentKey: 'acct-a=Solo_Bass|acct-b=Solo_Guitar' }),
      ]),
    }));
  });

  it('applies a repeated-instrument combo when the band has played it', async () => {
    const onApply = vi.fn();
    apiMock.getBandDetail.mockResolvedValue(makeBandDetail({ includeDoubleLead: true }));
    renderModal({ onApply });

    const leadButtons = await screen.findAllByTitle('Lead');
    fireEvent.click(leadButtons[0]!);
    fireEvent.click(leadButtons[1]!);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Apply' })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      comboId: 'Solo_Guitar+Solo_Guitar',
      assignments: [
        { accountId: 'acct-a', instrument: 'Solo_Guitar' },
        { accountId: 'acct-b', instrument: 'Solo_Guitar' },
      ],
      configurations: [expect.objectContaining({ assignmentKey: 'acct-a=Solo_Guitar|acct-b=Solo_Guitar' })],
    }));
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

  it('resets to a cleared draft without immediately applying or closing', async () => {
    const onApply = vi.fn();
    const onReset = vi.fn();
    renderModal({ appliedAssignments, onApply, onReset });

    await screen.findByText('Instrument #1');
    await waitFor(() => expect(instrumentSelectionScale('Lead', 0)).toBe('scale(1)'));
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Reset' }));

    expect(onReset).not.toHaveBeenCalled();
    expect(onApply).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    await waitFor(() => expect(instrumentSelectionScale('Lead', 0)).toBe('scale(0)'));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Apply' })).not.toBeDisabled());
  });

  it('applies a cleared draft through the reset callback', async () => {
    const onApply = vi.fn();
    const onReset = vi.fn();
    renderModal({ appliedAssignments, onApply, onReset });

    await screen.findByText('Instrument #1');
    await waitFor(() => expect(instrumentSelectionScale('Lead', 0)).toBe('scale(1)'));
    fireEvent.click(screen.getByRole('button', { name: 'Reset' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Apply' })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));

    expect(onReset).toHaveBeenCalledTimes(1);
    expect(onApply).not.toHaveBeenCalled();
  });

  it('keeps Apply disabled when reset is a no-op with no applied filter', async () => {
    const onReset = vi.fn();
    renderModal({ onReset });

    await screen.findByText('Instrument #1');
    fireEvent.click(screen.getByRole('button', { name: 'Reset' }));

    expect(onReset).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
  });
});

function makeBandDetail(options: { includeDoubleLead?: boolean } = {}) {
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
      {
        rawInstrumentCombo: '0:3',
        comboId: 'Solo_Guitar+Solo_Drums',
        instruments: ['Solo_Guitar', 'Solo_Drums'],
        assignmentKey: 'acct-a=Solo_Guitar|acct-b=Solo_Drums',
        appearanceCount: 1,
        memberInstruments: {
          'acct-a': 'Solo_Guitar',
          'acct-b': 'Solo_Drums',
        },
      },
      ...(options.includeDoubleLead ? [{
        rawInstrumentCombo: '0:0',
        comboId: 'Solo_Guitar+Solo_Guitar',
        instruments: ['Solo_Guitar', 'Solo_Guitar'],
        assignmentKey: 'acct-a=Solo_Guitar|acct-b=Solo_Guitar',
        appearanceCount: 1,
        memberInstruments: {
          'acct-a': 'Solo_Guitar',
          'acct-b': 'Solo_Guitar',
        },
      }] : []),
    ],
  };
}
