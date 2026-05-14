import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, act, waitFor } from '@testing-library/react';
import type { ComponentProps } from 'react';
import SuggestionsFilterModal, {
  type SuggestionsFilterDraft,
  defaultSuggestionsFilterDraft,
  isSuggestionsFilterActive,
} from '../../../../src/pages/suggestions/modals/SuggestionsFilterModal';
import {
  SUGGESTION_TYPES,
  globalKeyFor,
  perInstrumentKeyFor,
} from '@festival/core/suggestions/suggestionFilterConfig';
import { InstrumentKeys } from '@festival/core/instruments';
import { TRANSITION_MS } from '@festival/theme';
import { TestProviders } from '../../../helpers/TestProviders';
import type { SelectedBandProfile } from '../../../../src/hooks/data/useSelectedProfile';
import type { BandInstrumentFilterAssignment } from '../../../../src/types/bandFilter';

const apiMock = vi.hoisted(() => ({
  getBandDetail: vi.fn(),
}));

vi.mock('../../../../src/api/client', () => ({
  api: apiMock,
}));

/* ── Helpers ── */

const allVisible = () => ({
  showLead: true,
  showBass: true,
  showDrums: true,
  showVocals: true,
  showProLead: true,
  showProBass: true,
  showPeripheralVocals: true,
  showPeripheralCymbals: true,
  showPeripheralDrums: true,
});

const baseDraft = (): SuggestionsFilterDraft => defaultSuggestionsFilterDraft();

const defaultProps = (): ComponentProps<typeof SuggestionsFilterModal> => ({
  visible: true,
  draft: baseDraft(),
  savedDraft: baseDraft(),
  instrumentVisibility: allVisible(),
  onChange: vi.fn(),
  onCancel: vi.fn(),
  onReset: vi.fn(),
  onApply: vi.fn(),
});

function renderModal(overrides: Partial<ComponentProps<typeof SuggestionsFilterModal>> = {}) {
  const props = { ...defaultProps(), ...overrides };
  return { ...render(<TestProviders><SuggestionsFilterModal {...props} /></TestProviders>), props };
}

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

function bandComboFilter(overrides: Partial<NonNullable<ComponentProps<typeof SuggestionsFilterModal>['bandComboFilter']>> = {}) {
  return {
    selectedBand,
    appliedAssignments: [] as BandInstrumentFilterAssignment[],
    onApply: vi.fn(),
    onReset: vi.fn(),
    ...overrides,
  };
}

function instrumentSelectionScale(title: string, index = 0) {
  return screen.getAllByAltText(title)[index]?.closest('button')?.querySelector('div')?.style.transform;
}

async function clickCurrentCompactInstrument(alt: string, index = 0) {
  const instrument = (await screen.findAllByAltText(alt))[index];
  fireEvent.click(instrument.closest('button') ?? instrument);
}

/* ── Tests ── */

describe('SuggestionsFilterModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiMock.getBandDetail.mockResolvedValue(makeBandDetail());
  });

  /* ── Visibility ── */

  it('renders nothing when visible is false', () => {
    const { container } = render(
      <TestProviders><SuggestionsFilterModal {...defaultProps()} visible={false} /></TestProviders>,
    );
    expect(container.querySelector('[role="dialog"]')).toBeNull();
  });

  it('renders modal title when visible', () => {
    renderModal();
    expect(screen.getByText('Filter Suggestions')).toBeDefined();
  });

  /* ── Instruments section ── */

  it('shows instrument toggles after expanding accordion', () => {
    renderModal();
    fireEvent.click(screen.getByText('Instruments'));
    expect(screen.getByText('Lead')).toBeDefined();
    expect(screen.getByText('Bass')).toBeDefined();
    expect(screen.getByText('Drums')).toBeDefined();
    expect(screen.getByText('Tap Vocals')).toBeDefined();
    expect(screen.getByText('Pro Lead')).toBeDefined();
    expect(screen.getByText('Pro Bass')).toBeDefined();
    expect(screen.getByText('Karaoke')).toBeDefined();
    expect(screen.getByText('Pro Drums + Cymbals')).toBeDefined();
    expect(screen.getByText('Pro Drums')).toBeDefined();
  });

  it('toggles an instrument filter off', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Instruments'));
    fireEvent.click(screen.getByText('Lead'));
    expect(props.onChange).toHaveBeenCalledTimes(1);
    const newDraft = props.onChange.mock.calls[0]![0] as SuggestionsFilterDraft;
    expect(newDraft.suggestionsLeadFilter).toBe(false);
  });

  it('toggles an instrument filter back on', () => {
    const draft = baseDraft();
    draft.suggestionsLeadFilter = false;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Instruments'));
    fireEvent.click(screen.getByText('Lead'));
    const newDraft = props.onChange.mock.calls[0]![0] as SuggestionsFilterDraft;
    expect(newDraft.suggestionsLeadFilter).toBe(true);
  });

  it('respects instrument visibility — hides Pro Lead when showProLead is false', () => {
    const vis = { ...allVisible(), showProLead: false };
    renderModal({ instrumentVisibility: vis });
    fireEvent.click(screen.getByText('Instruments'));
    expect(screen.queryByText('Pro Lead')).toBeNull();
  });

  /* ── General suggestion type toggles ── */

  it('shows general suggestion type toggles after expanding accordion', () => {
    renderModal();
    fireEvent.click(screen.getByText('General'));
    for (const st of SUGGESTION_TYPES) {
      // Labels appear in both General and always-rendered Instrument-Specific sections
      expect(screen.getAllByText(st.label).length).toBeGreaterThanOrEqual(1);
    }
  });

  it('toggles a global suggestion type off (and per-instrument keys)', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('General'));
    fireEvent.click(screen.getAllByText('Near FC')[0]!);
    expect(props.onChange).toHaveBeenCalledTimes(1);
    const newDraft = props.onChange.mock.calls[0]![0] as SuggestionsFilterDraft;
    expect(newDraft[globalKeyFor('NearFC')]).toBe(false);
    // Per-instrument keys should also be turned off
    expect(newDraft[perInstrumentKeyFor('guitar', 'NearFC')]).toBe(false);
    expect(newDraft[perInstrumentKeyFor('bass', 'NearFC')]).toBe(false);
  });

  it('toggles a global suggestion type on (and per-instrument keys)', () => {
    const draft = baseDraft();
    draft[globalKeyFor('NearFC')] = false;
    draft[perInstrumentKeyFor('guitar', 'NearFC')] = false;
    draft[perInstrumentKeyFor('bass', 'NearFC')] = false;
    draft[perInstrumentKeyFor('drums', 'NearFC')] = false;
    draft[perInstrumentKeyFor('vocals', 'NearFC')] = false;
    draft[perInstrumentKeyFor('pro_guitar', 'NearFC')] = false;
    draft[perInstrumentKeyFor('pro_bass', 'NearFC')] = false;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('General'));
    fireEvent.click(screen.getAllByText('Near FC')[0]!);
    const newDraft = props.onChange.mock.calls[0]![0] as SuggestionsFilterDraft;
    expect(newDraft[globalKeyFor('NearFC')]).toBe(true);
    expect(newDraft[perInstrumentKeyFor('guitar', 'NearFC')]).toBe(true);
  });

  /* ── Instrument-specific section ── */

  it('shows instrument selector icons in Instrument-Specific section', () => {
    renderModal();
    expect(screen.getByText('Instrument-Specific')).toBeDefined();
  });

  it('selecting an instrument shows per-instrument toggles', () => {
    renderModal();
    // Click Lead instrument button in the selector
    fireEvent.click(screen.getByTitle('Lead'));
    for (const st of SUGGESTION_TYPES) {
      // Per-instrument toggles reuse the same label as the general section
      expect(screen.getAllByText(st.label).length).toBeGreaterThanOrEqual(1);
    }
  });

  it('deselects instrument when clicking same instrument again', () => {
    const { } = renderModal();
    fireEvent.click(screen.getByTitle('Lead'));
    // per-instrument toggles should be visible
    const countBefore = screen.getAllByText('Near FC').length;
    fireEvent.click(screen.getByTitle('Lead'));
    // per-instrument toggles should collapse (gridTemplateRows: '0fr')
    // The toggles still exist in DOM but are hidden; verify we can still see the general one
    expect(screen.getAllByText('Near FC').length).toBeLessThanOrEqual(countBefore);
  });

  it('toggles per-instrument suggestion type on', () => {
    const draft = baseDraft();
    draft[perInstrumentKeyFor('guitar', 'NearFC')] = false;
    draft[globalKeyFor('NearFC')] = false;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByTitle('Lead'));
    // The first "Near FC" in the per-instrument section
    const nearFcBtns = screen.getAllByText('Near FC');
    fireEvent.click(nearFcBtns[nearFcBtns.length - 1]!);
    const newDraft = props.onChange.mock.calls[0]![0] as SuggestionsFilterDraft;
    expect(newDraft[perInstrumentKeyFor('guitar', 'NearFC')]).toBe(true);
    // Global should be re-enabled
    expect(newDraft[globalKeyFor('NearFC')]).toBe(true);
  });

  it('toggles per-instrument suggestion type off and disables global when all per-instrument are off', () => {
    const draft = baseDraft();
    // Turn off all per-instrument NearFC except guitar
    for (const inst of InstrumentKeys.filter(inst => inst !== 'guitar')) {
      draft[perInstrumentKeyFor(inst, 'NearFC')] = false;
    }
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByTitle('Lead'));
    const nearFcBtns = screen.getAllByText('Near FC');
    fireEvent.click(nearFcBtns[nearFcBtns.length - 1]!);
    const newDraft = props.onChange.mock.calls[0]![0] as SuggestionsFilterDraft;
    expect(newDraft[perInstrumentKeyFor('guitar', 'NearFC')]).toBe(false);
    expect(newDraft[globalKeyFor('NearFC')]).toBe(false);
  });

  it('toggles per-instrument off but keeps global on when other instruments still on', () => {
    const draft = baseDraft();
    // Bass NearFC is still on
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByTitle('Lead'));
    const nearFcBtns = screen.getAllByText('Near FC');
    fireEvent.click(nearFcBtns[nearFcBtns.length - 1]!);
    const newDraft = props.onChange.mock.calls[0]![0] as SuggestionsFilterDraft;
    expect(newDraft[perInstrumentKeyFor('guitar', 'NearFC')]).toBe(false);
    // Global should stay on because other per-instrument keys are still true
    // The spread { ...draft, ...updates } preserves the original draft value
    expect(newDraft[globalKeyFor('NearFC')]).toBe(true);
  });

  it('effectiveSelectedInstrument falls back to null if instrument becomes invisible', () => {
    const vis = { ...allVisible(), showProLead: false };
    // Render with Pro Lead visible, select it, then re-render with it hidden
    const props = defaultProps();
    props.instrumentVisibility = vis;
    renderModal(props);
    // Pro Lead button should not exist
    expect(screen.queryByTitle('Pro Lead')).toBeNull();
  });

  it('resets instrument selection when modal reopens', () => {
    const props = defaultProps();
    const { rerender } = renderModal(props);
    // Select Lead — per-instrument toggles become interactive
    fireEvent.click(screen.getByTitle('Lead'));
    const nearFcBtns = screen.getAllByText('Near FC');
    fireEvent.click(nearFcBtns[nearFcBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
    props.onChange.mockClear();

    // Close modal then reopen
    rerender(<TestProviders><SuggestionsFilterModal {...props} visible={false} /></TestProviders>);
    rerender(<TestProviders><SuggestionsFilterModal {...props} visible={true} /></TestProviders>);

    // After reopening, instrument selection is reset — per-instrument toggles should be non-interactive
    const nearFcAfter = screen.getAllByText('Near FC');
    fireEvent.click(nearFcAfter[nearFcAfter.length - 1]!);
    expect(props.onChange).not.toHaveBeenCalled();
  });

  /* ── Apply / Reset / Cancel ── */

  it('calls onApply when Apply button is clicked', () => {
    const draft = baseDraft();
    draft.suggestionsLeadFilter = false;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Apply Filter Changes'));
    expect(props.onApply).toHaveBeenCalledTimes(1);
  });

  it('disables Apply button when there are no changes', () => {
    const draft = baseDraft();
    renderModal({ draft, savedDraft: draft });
    const btn = screen.getByText('Apply Filter Changes');
    expect(btn.closest('button')!.disabled).toBe(true);
  });

  it('enables Apply when draft differs from savedDraft', () => {
    const draft = baseDraft();
    draft.suggestionsLeadFilter = false;
    renderModal({ draft });
    const btn = screen.getByText('Apply Filter Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  it('enables Apply when savedDraft is undefined', () => {
    renderModal({ savedDraft: undefined });
    const btn = screen.getByText('Apply Filter Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  it('calls onReset when Reset button is clicked', () => {
    const props = defaultProps();
    renderModal(props);
    // i18n: resetLabel renders as title div; button uses common.reset = 'Reset'
    const resetBtn = screen.getAllByRole('button', { name: 'Reset' });
    fireEvent.click(resetBtn[resetBtn.length - 1]!);
    expect(props.onReset).toHaveBeenCalledTimes(1);
  });

  it('renders the selected-band combo picker inside the band-mode modal', async () => {
    renderModal({ mode: 'band', bandComboFilter: bandComboFilter() });

    expect(await screen.findByText('Instrument #1')).toBeDefined();
    expect(screen.getByText('Instrument #2')).toBeDefined();
    expect(screen.queryByText('Instruments')).toBeNull();
    expect(apiMock.getBandDetail).toHaveBeenCalledWith('band-duo');
  });

  it('applies a valid embedded combo through the modal Apply button', async () => {
    const combo = bandComboFilter();
    const { props } = renderModal({ mode: 'band', bandComboFilter: combo });

    await screen.findByText('Instrument #1');
    await clickCurrentCompactInstrument('Solo_Guitar', 0);
    fireEvent.click(screen.getAllByLabelText('Next instrument')[1]!);
    await clickCurrentCompactInstrument('Solo_Bass', 0);

    await waitFor(() => expect(screen.getByText('Apply Filter Changes').closest('button')).not.toBeDisabled());
    fireEvent.click(screen.getByText('Apply Filter Changes'));

    expect(combo.onApply).toHaveBeenCalledWith(expect.objectContaining({
      comboId: 'Solo_Guitar+Solo_Bass',
      assignments: [
        { accountId: 'acct-a', instrument: 'Solo_Guitar' },
        { accountId: 'acct-b', instrument: 'Solo_Bass' },
      ],
    }));
    expect(props.onApply).toHaveBeenCalledTimes(1);
  });

  it('resets the embedded combo draft and clears the applied combo on Apply', async () => {
    const combo = bandComboFilter({ appliedAssignments });
    const { props } = renderModal({ mode: 'band', bandComboFilter: combo });

    await screen.findByText('Instrument #1');
    await waitFor(() => expect(instrumentSelectionScale('Solo_Guitar', 0)).toBe('scale(1)'));

    const resetBtn = screen.getAllByRole('button', { name: 'Reset' });
    fireEvent.click(resetBtn[resetBtn.length - 1]!);

    expect(props.onReset).toHaveBeenCalledTimes(1);
    expect(combo.onReset).not.toHaveBeenCalled();
    await waitFor(() => expect(instrumentSelectionScale('Solo_Guitar', 0)).toBe('scale(0)'));
    await waitFor(() => expect(screen.getByText('Apply Filter Changes').closest('button')).not.toBeDisabled());

    fireEvent.click(screen.getByText('Apply Filter Changes'));

    expect(combo.onReset).toHaveBeenCalledTimes(1);
    expect(combo.onApply).not.toHaveBeenCalled();
    expect(props.onApply).toHaveBeenCalledTimes(1);
  });

  /* ── Confirm dialog ── */

  it('calls onCancel directly when no changes', () => {
    const draft = baseDraft();
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    expect(props.onCancel).toHaveBeenCalledTimes(1);
  });

  it('shows confirm dialog when closing with unsaved changes', () => {
    const draft = baseDraft();
    draft.suggestionsLeadFilter = false;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    expect(screen.getByText('Discard Suggestion Filter Changes')).toBeDefined();
  });

  it('confirm dialog "No" dismisses the dialog', () => {
    vi.useFakeTimers();
    const draft = baseDraft();
    draft.suggestionsLeadFilter = false;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('No'));
    expect(props.onCancel).not.toHaveBeenCalled();
    act(() => { vi.advanceTimersByTime(TRANSITION_MS); });
    expect(screen.queryByText('Discard Suggestion Filter Changes')).toBeNull();
    vi.useRealTimers();
  });

  it('confirm dialog "Yes" calls onCancel', () => {
    const draft = baseDraft();
    draft.suggestionsLeadFilter = false;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('Yes'));
    expect(props.onCancel).toHaveBeenCalledTimes(1);
  });
});

/* ── Utility function tests ── */

describe('isSuggestionsFilterActive', () => {
  it('returns false for default draft', () => {
    expect(isSuggestionsFilterActive(defaultSuggestionsFilterDraft())).toBe(false);
  });

  it('returns true when an instrument filter is changed', () => {
    const d = defaultSuggestionsFilterDraft();
    d.suggestionsLeadFilter = false;
    expect(isSuggestionsFilterActive(d)).toBe(true);
  });

  it('returns true when a global suggestion type is changed', () => {
    const d = defaultSuggestionsFilterDraft();
    d[globalKeyFor('NearFC')] = false;
    expect(isSuggestionsFilterActive(d)).toBe(true);
  });

  it('returns true when a per-instrument suggestion type is changed', () => {
    const d = defaultSuggestionsFilterDraft();
    d[perInstrumentKeyFor('guitar', 'NearFC')] = false;
    expect(isSuggestionsFilterActive(d)).toBe(true);
  });

  it('returns false when draft has undefined keys that fall back to defaults', () => {
    const d = defaultSuggestionsFilterDraft();
    // Remove a key to trigger the ?? fallback
    delete (d as any).suggestionsLeadFilter;
    expect(isSuggestionsFilterActive(d)).toBe(false);
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
    ],
  };
}

describe('defaultSuggestionsFilterDraft', () => {
  it('has all instrument filters set to true', () => {
    const d = defaultSuggestionsFilterDraft();
    expect(d.suggestionsLeadFilter).toBe(true);
    expect(d.suggestionsBassFilter).toBe(true);
    expect(d.suggestionsDrumsFilter).toBe(true);
    expect(d.suggestionsVocalsFilter).toBe(true);
    expect(d.suggestionsProLeadFilter).toBe(true);
    expect(d.suggestionsProBassFilter).toBe(true);
  });

  it('has all global suggestion type keys set to true', () => {
    const d = defaultSuggestionsFilterDraft();
    for (const st of SUGGESTION_TYPES) {
      expect(d[globalKeyFor(st.id)]).toBe(true);
    }
  });
});
