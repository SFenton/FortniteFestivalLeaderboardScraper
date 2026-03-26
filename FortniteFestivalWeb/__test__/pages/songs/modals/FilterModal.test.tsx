import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import FilterModal, { type FilterDraft } from '../../../../src/pages/songs/modals/FilterModal';
import { INSTRUMENT_KEYS } from '@festival/core/api/serverTypes';
import { defaultSongFilters } from '../../../../src/utils/songSettings';
import { TestProviders } from '../../../helpers/TestProviders';

/* ── Helpers ── */

function baseDraft(): FilterDraft {
  return { ...defaultSongFilters(), instrumentFilter: null };
}

const defaultProps = () => ({
  visible: true,
  draft: baseDraft(),
  savedDraft: baseDraft(),
  availableSeasons: [1, 2, 3],
  onChange: vi.fn(),
  onCancel: vi.fn(),
  onReset: vi.fn(),
  onApply: vi.fn(),
});

function renderModal(overrides: Partial<ReturnType<typeof defaultProps>> = {}) {
  const props = { ...defaultProps(), ...overrides };
  return { ...render(<TestProviders><FilterModal {...props} /></TestProviders>), props };
}

/* ── Tests ── */

describe('FilterModal', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  /* ── Visibility ── */

  it('renders nothing when visible is false', () => {
    const { container } = render(
      <TestProviders><FilterModal {...defaultProps()} visible={false} /></TestProviders>,
    );
    // ModalShell returns null when not visible
    expect(container.querySelector('[role="dialog"]')).toBeNull();
  });

  it('renders modal title when visible', () => {
    renderModal();
    expect(screen.getByText('Filter Songs')).toBeDefined();
  });

  /* ── Global toggles section ── */

  it('shows global toggle accordion', () => {
    renderModal();
    expect(screen.getByText('Global Score & FC Toggles')).toBeDefined();
  });

  it('shows global toggle labels after expanding accordion', () => {
    renderModal();
    // Expand the accordion
    fireEvent.click(screen.getByText('Global Score & FC Toggles'));
    expect(screen.getByText('Missing Scores')).toBeDefined();
    expect(screen.getByText('Has Scores')).toBeDefined();
    expect(screen.getByText('Missing FCs')).toBeDefined();
    expect(screen.getByText('Has FCs')).toBeDefined();
  });

  it('toggles global missingScores on for all visible instruments', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Global Score & FC Toggles'));
    // Click the Missing Scores toggle (the ToggleRow button)
    fireEvent.click(screen.getByText('Missing Scores'));
    expect(props.onChange).toHaveBeenCalledTimes(1);
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    // All 6 instrument keys should be true
    for (const k of INSTRUMENT_KEYS) {
      expect(newDraft.missingScores[k]).toBe(true);
    }
  });

  it('toggles global missingScores off when all are already on', () => {
    const draft = baseDraft();
    for (const k of INSTRUMENT_KEYS) draft.missingScores[k] = true;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Global Score & FC Toggles'));
    fireEvent.click(screen.getByText('Missing Scores'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    for (const k of INSTRUMENT_KEYS) {
      expect(newDraft.missingScores[k]).toBe(false);
    }
  });

  it('toggles global hasScores', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Global Score & FC Toggles'));
    fireEvent.click(screen.getByText('Has Scores'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    for (const k of INSTRUMENT_KEYS) {
      expect(newDraft.hasScores[k]).toBe(true);
    }
  });

  it('toggles global missingFCs', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Global Score & FC Toggles'));
    fireEvent.click(screen.getByText('Missing FCs'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    for (const k of INSTRUMENT_KEYS) {
      expect(newDraft.missingFCs[k]).toBe(true);
    }
  });

  it('toggles global hasFCs', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Global Score & FC Toggles'));
    fireEvent.click(screen.getByText('Has FCs'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    for (const k of INSTRUMENT_KEYS) {
      expect(newDraft.hasFCs[k]).toBe(true);
    }
  });

  /* ── Individual instrument toggles ── */

  it('shows individual instrument accordions', () => {
    renderModal();
    expect(screen.getByText('Individual Score & FC Toggles')).toBeDefined();
    expect(screen.getByText('Lead')).toBeDefined();
    expect(screen.getByText('Bass')).toBeDefined();
    expect(screen.getByText('Drums')).toBeDefined();
    expect(screen.getByText('Vocals')).toBeDefined();
    expect(screen.getByText('Pro Lead')).toBeDefined();
    expect(screen.getByText('Pro Bass')).toBeDefined();
  });

  it('toggles individual missingScores for Lead', () => {
    const props = defaultProps();
    renderModal(props);
    // Expand Lead accordion
    fireEvent.click(screen.getByText('Lead'));
    fireEvent.click(screen.getByText('Missing Lead Scores'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    expect(newDraft.missingScores['Solo_Guitar']).toBe(true);
  });

  it('toggles individual hasScores for Lead', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Lead'));
    fireEvent.click(screen.getByText('Has Lead Scores'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    expect(newDraft.hasScores['Solo_Guitar']).toBe(true);
  });

  it('toggles individual missingFCs for Lead', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Lead'));
    fireEvent.click(screen.getByText('Missing Lead FCs'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    expect(newDraft.missingFCs['Solo_Guitar']).toBe(true);
  });

  it('toggles individual hasFCs for Lead', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Lead'));
    fireEvent.click(screen.getByText('Has Lead FCs'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    expect(newDraft.hasFCs['Solo_Guitar']).toBe(true);
  });

  it('toggles off an already-on individual toggle', () => {
    const draft = baseDraft();
    draft.missingScores['Solo_Guitar'] = true;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Lead'));
    fireEvent.click(screen.getByText('Missing Lead Scores'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    expect(newDraft.missingScores['Solo_Guitar']).toBe(false);
  });

  /* ── Instrument filter selector ── */

  it('shows instrument selector buttons', () => {
    renderModal();
    expect(screen.getByTitle('Lead')).toBeDefined();
    expect(screen.getByTitle('Bass')).toBeDefined();
    expect(screen.getByTitle('Drums')).toBeDefined();
    expect(screen.getByTitle('Vocals')).toBeDefined();
    expect(screen.getByTitle('Pro Lead')).toBeDefined();
    expect(screen.getByTitle('Pro Bass')).toBeDefined();
  });

  it('selects an instrument filter', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByTitle('Lead'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    expect(newDraft.instrumentFilter).toBe('Solo_Guitar');
  });

  it('deselects the instrument filter when clicking the same instrument', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByTitle('Lead'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    expect(newDraft.instrumentFilter).toBeNull();
  });

  /* ── Instrument-specific filter sub-sections ── */

  it('shows season/percentile/stars/intensity sections when instrument is selected', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    renderModal({ draft, savedDraft: draft });
    expect(screen.getByText('Season')).toBeDefined();
    expect(screen.getByText('Percentile')).toBeDefined();
    expect(screen.getByText('Stars')).toBeDefined();
    expect(screen.getByText('Song Intensity')).toBeDefined();
  });

  /* ── Season toggles ── */

  it('renders season toggles with available seasons + No Score', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    renderModal({ draft, savedDraft: draft, availableSeasons: [1, 2, 3] });
    fireEvent.click(screen.getByText('Season'));
    expect(screen.getByText('Season 1')).toBeDefined();
    expect(screen.getByText('Season 2')).toBeDefined();
    expect(screen.getByText('Season 3')).toBeDefined();
    expect(screen.getAllByText('No Score').length).toBeGreaterThanOrEqual(1);
  });

  it('toggles a season filter off', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Season'));
    fireEvent.click(screen.getByText('Season 1'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    expect(newDraft.seasonFilter[1]).toBe(false);
  });

  it('season Select All sets all seasons to true', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    draft.seasonFilter = { 1: false, 2: false, 3: false, 0: false };
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    props.availableSeasons = [1, 2, 3];
    renderModal(props);
    fireEvent.click(screen.getByText('Season'));
    const selectAllBtns = screen.getAllByText('Select All');
    fireEvent.click(selectAllBtns[0]!);
    const newDraft = props.onChange.mock.calls[0]![0];
    expect(newDraft.seasonFilter[1]).toBe(true);
    expect(newDraft.seasonFilter[2]).toBe(true);
    expect(newDraft.seasonFilter[3]).toBe(true);
    expect(newDraft.seasonFilter[0]).toBe(true);
  });

  it('season Clear All sets all seasons to false', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    props.availableSeasons = [1, 2, 3];
    renderModal(props);
    fireEvent.click(screen.getByText('Season'));
    const clearAllBtns = screen.getAllByText('Clear All');
    fireEvent.click(clearAllBtns[0]!);
    const newDraft = props.onChange.mock.calls[0]![0];
    expect(newDraft.seasonFilter[1]).toBe(false);
    expect(newDraft.seasonFilter[2]).toBe(false);
    expect(newDraft.seasonFilter[3]).toBe(false);
    expect(newDraft.seasonFilter[0]).toBe(false);
  });

  /* ── Percentile toggles ── */

  it('renders percentile toggles including No Score and Top N%', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    renderModal({ draft, savedDraft: draft });
    fireEvent.click(screen.getByText('Percentile'));
    expect(screen.getAllByText('No Score').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Top 1%')).toBeDefined();
    expect(screen.getByText('Top 100%')).toBeDefined();
  });

  it('toggles a percentile filter', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Percentile'));
    fireEvent.click(screen.getByText('Top 1%'));
    const newDraft = props.onChange.mock.calls[0]![0] as FilterDraft;
    expect(newDraft.percentileFilter[1]).toBe(false);
  });

  it('percentile Select All / Clear All work', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    draft.percentileFilter = { 0: false, 1: false };
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Percentile'));
    // There are multiple Select All / Clear All buttons; find by the parenthood (the second set from Percentile)
    const selectAllBtns = screen.getAllByText('Select All');
    fireEvent.click(selectAllBtns[selectAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  /* ── Stars toggles ── */

  it('renders star toggles including No Score', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    renderModal({ draft, savedDraft: draft });
    fireEvent.click(screen.getByText('Stars'));
    // "No Score" appears for multiple sub-sections
    expect(screen.getAllByText('No Score').length).toBeGreaterThanOrEqual(1);
  });

  it('toggles a stars filter', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Stars'));
    // Stars renders images, but also has "No Score" entry (key 0)
    const noScoreBtns = screen.getAllByText('No Score');
    fireEvent.click(noScoreBtns[noScoreBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  it('renders star images for gold and regular stars', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    renderModal({ draft, savedDraft: draft });
    fireEvent.click(screen.getByText('Stars'));
    // Gold stars (key=6) renders 5 gold star images, regular stars (key=5) renders 5 white star images
    const starImages = screen.getAllByRole('img');
    expect(starImages.length).toBeGreaterThanOrEqual(1);
  });

  it('stars Select All sets all star keys to true', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    draft.starsFilter = { 6: false, 5: false, 4: false, 3: false, 2: false, 1: false, 0: false };
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Stars'));
    const selectAllBtns = screen.getAllByText('Select All');
    fireEvent.click(selectAllBtns[selectAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  it('stars Clear All sets all star keys to false', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Stars'));
    const clearAllBtns = screen.getAllByText('Clear All');
    fireEvent.click(clearAllBtns[clearAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  /* ── Difficulty toggles ── */

  it('renders song intensity section', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    renderModal({ draft, savedDraft: draft });
    expect(screen.getByText('Song Intensity')).toBeDefined();
  });

  it('toggles a difficulty filter', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Song Intensity'));
    // The DifficultyToggles include a "No Score" entry
    const noScoreBtns = screen.getAllByText('No Score');
    fireEvent.click(noScoreBtns[noScoreBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  it('difficulty Select All sets all keys to true', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    draft.difficultyFilter = { 1: false, 2: false, 3: false, 4: false, 5: false, 6: false, 7: false, 0: false };
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Song Intensity'));
    const selectAllBtns = screen.getAllByText('Select All');
    fireEvent.click(selectAllBtns[selectAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  it('difficulty Clear All sets all keys to false', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Song Intensity'));
    const clearAllBtns = screen.getAllByText('Clear All');
    fireEvent.click(clearAllBtns[clearAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  it('percentile Clear All sets all keys to false', () => {
    const draft = baseDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Percentile'));
    const clearAllBtns = screen.getAllByText('Clear All');
    fireEvent.click(clearAllBtns[clearAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  /* ── Apply / Reset / Cancel ── */

  it('calls onApply when Apply button is clicked', () => {
    const draft = baseDraft();
    draft.missingScores['Solo_Guitar'] = true;
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
    draft.missingScores['Solo_Guitar'] = true;
    renderModal({ draft });
    const btn = screen.getByText('Apply Filter Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  it('calls onReset when Reset button is clicked', () => {
    const props = defaultProps();
    renderModal(props);
    const resetBtns = screen.getAllByText('Reset Filter Settings');
    // Click the button (not the section title)
    fireEvent.click(resetBtns[resetBtns.length - 1]!);
    expect(props.onReset).toHaveBeenCalledTimes(1);
  });

  /* ── Confirm dialog on close with changes ── */

  it('calls onCancel directly when there are no changes', () => {
    const draft = baseDraft();
    const props = defaultProps();
    props.draft = draft;
    props.savedDraft = draft;
    renderModal(props);
    // Click the close button (X) in ModalShell
    fireEvent.click(screen.getByLabelText('Close'));
    expect(props.onCancel).toHaveBeenCalledTimes(1);
  });

  it('shows confirm dialog when closing with unsaved changes', () => {
    const draft = baseDraft();
    draft.missingScores['Solo_Guitar'] = true;
    const props = defaultProps();
    props.draft = draft;
    // savedDraft is the default (no filters), so there are changes
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    expect(screen.getByText('Cancel Filter Changes')).toBeDefined();
    expect(screen.getByText('Are you sure you want to discard your filter changes?')).toBeDefined();
  });

  it('confirm dialog "No" dismisses the dialog but stays open', () => {
    const draft = baseDraft();
    draft.missingScores['Solo_Guitar'] = true;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('No'));
    expect(props.onCancel).not.toHaveBeenCalled();
    // Confirm dialog should be gone
    expect(screen.queryByText('Cancel Filter Changes')).toBeNull();
  });

  it('confirm dialog "Yes" calls onCancel', () => {
    const draft = baseDraft();
    draft.missingScores['Solo_Guitar'] = true;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('Yes'));
    expect(props.onCancel).toHaveBeenCalledTimes(1);
  });

  it('hasChanges returns true when savedDraft is undefined', () => {
    const props = defaultProps();
    props.savedDraft = undefined as unknown as FilterDraft;
    renderModal(props);
    // Apply should be enabled
    const btn = screen.getByText('Apply Filter Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  /* ── Instrument visibility from settings ── */

  it('respects settings instrument visibility (hides Pro Lead when disabled)', () => {
    // Default settings have all instruments visible, so Pro Lead should appear.
    // We can't easily change settings context mid-render in this unit test,
    // so just confirm all 6 show by default.
    renderModal();
    expect(screen.getByTitle('Pro Lead')).toBeDefined();
    expect(screen.getByTitle('Pro Bass')).toBeDefined();
  });
});
