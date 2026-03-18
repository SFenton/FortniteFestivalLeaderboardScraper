/**
 * Tests for SortModal and FilterModal callback functions.
 * Exercises: setMode, handleClose, confirmDiscard, applySort, resetSort,
 * toggleGlobal, toggleMissing/Has per-instrument, selectInstrument, etc.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import SortModal, { type SortDraft } from '../../pages/songs/modals/SortModal';
import FilterModal, { type FilterDraft } from '../../pages/songs/modals/FilterModal';
import { INSTRUMENT_KEYS, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { DEFAULT_METADATA_ORDER, defaultSongFilters } from '../../utils/songSettings';
import { TestProviders } from '../helpers/TestProviders';

/* ── Sort Modal Helpers ── */

function baseSortDraft(): SortDraft {
  return { sortMode: 'title', sortAscending: true, metadataOrder: [...DEFAULT_METADATA_ORDER], instrumentOrder: [...INSTRUMENT_KEYS] };
}

function renderSortModal(overrides: Partial<Parameters<typeof SortModal>[0]> = {}) {
  const defaults = {
    visible: true,
    draft: baseSortDraft(),
    savedDraft: baseSortDraft(),
    instrumentFilter: null as InstrumentKey | null,
    hasPlayer: true,
    metadataVisibility: undefined as any,
    onChange: vi.fn(),
    onCancel: vi.fn(),
    onReset: vi.fn(),
    onApply: vi.fn(),
  };
  const props = { ...defaults, ...overrides };
  return { ...render(<TestProviders><SortModal {...props} /></TestProviders>), props };
}

/* ── Filter Modal Helpers ── */

function baseFilterDraft(): FilterDraft {
  return { ...defaultSongFilters(), instrumentFilter: null };
}

function renderFilterModal(overrides: Partial<Parameters<typeof FilterModal>[0]> = {}) {
  const defaults = {
    visible: true,
    draft: baseFilterDraft(),
    savedDraft: baseFilterDraft(),
    availableSeasons: [1, 2, 3],
    onChange: vi.fn(),
    onCancel: vi.fn(),
    onReset: vi.fn(),
    onApply: vi.fn(),
  };
  const props = { ...defaults, ...overrides };
  return { ...render(<TestProviders><FilterModal {...props} /></TestProviders>), props };
}

/* ══════════════════════════════════════════════
   SortModal — all uncovered function callbacks
   ══════════════════════════════════════════════ */

describe('SortModal — callback function coverage', () => {
  beforeEach(() => vi.clearAllMocks());

  it('setMode is called for every mode in hasPlayer=true accordion', () => {
    const { props } = renderSortModal({ hasPlayer: true });
    fireEvent.click(screen.getByText('Mode'));
    fireEvent.click(screen.getByText('Title'));
    fireEvent.click(screen.getByText('Artist'));
    fireEvent.click(screen.getByText('Year'));
    fireEvent.click(screen.getByText('Has FC'));
    expect(props.onChange).toHaveBeenCalledTimes(4);
  });

  it('setMode via hasPlayer=false flat section covers separate inline fns', () => {
    const { props } = renderSortModal({ hasPlayer: false });
    fireEvent.click(screen.getByText('Title'));
    fireEvent.click(screen.getByText('Artist'));
    fireEvent.click(screen.getByText('Year'));
    fireEvent.click(screen.getByText('Has FC'));
    expect(props.onChange).toHaveBeenCalledTimes(4);
  });

  it('handleClose calls onCancel when no changes', () => {
    const { props } = renderSortModal();
    fireEvent.click(screen.getByLabelText('Close'));
    expect(props.onCancel).toHaveBeenCalled();
  });

  it('handleClose shows confirm when changes exist', () => {
    const draft = baseSortDraft();
    draft.sortMode = 'artist';
    renderSortModal({ draft });
    fireEvent.click(screen.getByLabelText('Close'));
    expect(screen.getByText('Cancel Song Sort Changes')).toBeTruthy();
  });

  it('confirmDiscard calls onCancel from confirm dialog', () => {
    const draft = baseSortDraft();
    draft.sortMode = 'artist';
    const { props } = renderSortModal({ draft });
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('Yes'));
    expect(props.onCancel).toHaveBeenCalled();
  });

  it('confirm No dismisses dialog', () => {
    const draft = baseSortDraft();
    draft.sortMode = 'artist';
    renderSortModal({ draft });
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('No'));
    expect(screen.queryByText('Cancel Song Sort Changes')).toBeNull();
  });

  it('onApply called when Apply button clicked', () => {
    const draft = baseSortDraft();
    draft.sortMode = 'artist';
    const { props } = renderSortModal({ draft });
    fireEvent.click(screen.getByText('Apply Sort Changes'));
    expect(props.onApply).toHaveBeenCalled();
  });

  it('onReset called when Reset button clicked', () => {
    const { props } = renderSortModal();
    const resetBtns = screen.getAllByText('Reset Sort Settings');
    fireEvent.click(resetBtns[resetBtns.length - 1]!);
    expect(props.onReset).toHaveBeenCalled();
  });

  it('direction ascending/descending toggle', () => {
    const { props } = renderSortModal();
    fireEvent.click(screen.getByLabelText('Descending'));
    expect((props.onChange as any).mock.calls[0]![0].sortAscending).toBe(false);
    (props.onChange as any).mockClear();
    fireEvent.click(screen.getByLabelText('Ascending'));
    expect((props.onChange as any).mock.calls[0]![0].sortAscending).toBe(true);
  });

  it('renders metadata priority section when instrumentFilter is set', () => {
    renderSortModal({ instrumentFilter: 'Solo_Guitar' });
    expect(screen.getByText('Metadata Sort Priority')).toBeTruthy();
  });

  it('shows instrument order when hasfc mode and no instrument filter', () => {
    const draft = baseSortDraft();
    draft.sortMode = 'hasfc';
    renderSortModal({ draft, savedDraft: draft });
    expect(screen.getByText('Primary Instrument Order')).toBeTruthy();
  });

  it('enables Apply when savedDraft is undefined', () => {
    renderSortModal({ savedDraft: undefined });
    const btn = screen.getByText('Apply Sort Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  it('hasPlayer=false shows flat mode section', () => {
    renderSortModal({ hasPlayer: false });
    expect(screen.getByText('Title')).toBeTruthy();
    expect(screen.getByText('Artist')).toBeTruthy();
  });

  it('renders instrument sort modes and clicks one', () => {
    const { props } = renderSortModal({ instrumentFilter: 'Solo_Guitar' });
    fireEvent.click(screen.getByText('Filtered Instrument Sort Mode'));
    const scoreBtns = screen.getAllByText('Score');
    fireEvent.click(scoreBtns[0]!);
    expect((props.onChange as any).mock.calls[0]![0].sortMode).toBe('score');
  });
});

/* ══════════════════════════════════════════════
   FilterModal — all uncovered function callbacks
   ══════════════════════════════════════════════ */

describe('FilterModal — callback function coverage', () => {
  beforeEach(() => vi.clearAllMocks());

  it('toggleGlobal: toggles all visible missingScores on', () => {
    const { props } = renderFilterModal();
    fireEvent.click(screen.getByText('Global Score & FC Toggles'));
    fireEvent.click(screen.getByText('Missing Scores'));
    expect(props.onChange).toHaveBeenCalled();
  });

  it('toggleGlobal: toggles all visible hasScores', () => {
    const { props } = renderFilterModal();
    fireEvent.click(screen.getByText('Global Score & FC Toggles'));
    fireEvent.click(screen.getByText('Has Scores'));
    expect(props.onChange).toHaveBeenCalled();
  });

  it('toggleGlobal: toggles missingFCs', () => {
    const { props } = renderFilterModal();
    fireEvent.click(screen.getByText('Global Score & FC Toggles'));
    fireEvent.click(screen.getByText('Missing FCs'));
    expect(props.onChange).toHaveBeenCalled();
  });

  it('toggleGlobal: toggles hasFCs', () => {
    const { props } = renderFilterModal();
    fireEvent.click(screen.getByText('Global Score & FC Toggles'));
    fireEvent.click(screen.getByText('Has FCs'));
    expect(props.onChange).toHaveBeenCalled();
  });

  it('selectInstrument: selects Lead instrument filter', () => {
    const { props } = renderFilterModal();
    fireEvent.click(screen.getByTitle('Lead'));
    expect((props.onChange as any).mock.calls[0]![0].instrumentFilter).toBe('Solo_Guitar');
  });

  it('handleClose: calls onCancel when no changes', () => {
    const { props } = renderFilterModal();
    fireEvent.click(screen.getByLabelText('Close'));
    expect(props.onCancel).toHaveBeenCalled();
  });

  it('handleClose: shows confirm when changes exist', () => {
    const draft = baseFilterDraft();
    draft.missingScores = { Solo_Guitar: true };
    renderFilterModal({ draft });
    fireEvent.click(screen.getByLabelText('Close'));
    expect(screen.getByText('Cancel Song Filter Changes')).toBeTruthy();
  });

  it('confirmDiscard from confirm dialog', () => {
    const draft = baseFilterDraft();
    draft.missingScores = { Solo_Guitar: true };
    const { props } = renderFilterModal({ draft });
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('Yes'));
    expect(props.onCancel).toHaveBeenCalled();
  });

  it('onApply called when Apply button clicked', () => {
    const draft = baseFilterDraft();
    draft.missingScores = { Solo_Guitar: true };
    const { props } = renderFilterModal({ draft });
    fireEvent.click(screen.getByText('Apply Filter Changes'));
    expect(props.onApply).toHaveBeenCalled();
  });

  it('onReset called when Reset button clicked', () => {
    const { props } = renderFilterModal();
    const resetBtns = screen.getAllByText('Reset Filter Settings');
    fireEvent.click(resetBtns[resetBtns.length - 1]!);
    expect(props.onReset).toHaveBeenCalled();
  });

  it('individual instrument toggle: Lead missingScores', () => {
    const { props } = renderFilterModal();
    fireEvent.click(screen.getByText('Lead'));
    fireEvent.click(screen.getByText('Missing Lead Scores'));
    expect(props.onChange).toHaveBeenCalled();
  });

  it('shows season/percentile/stars/intensity when instrument selected', () => {
    const draft = baseFilterDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    renderFilterModal({ draft, savedDraft: draft });
    expect(screen.getByText('Season')).toBeTruthy();
    expect(screen.getByText('Percentile')).toBeTruthy();
    expect(screen.getByText('Stars')).toBeTruthy();
    expect(screen.getByText('Song Intensity')).toBeTruthy();
  });

  it('stars toggle: toggles a star key and clears all', () => {
    const draft = baseFilterDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const { props } = renderFilterModal({ draft, savedDraft: draft });
    fireEvent.click(screen.getByText('Stars'));
    const noScoreBtns = screen.getAllByText('No Score');
    fireEvent.click(noScoreBtns[noScoreBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  it('stars Select All and Clear All exercise selectAll/clearAll functions', () => {
    const draft = baseFilterDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const { props } = renderFilterModal({ draft, savedDraft: draft });
    fireEvent.click(screen.getByText('Stars'));
    const selectAllBtns = screen.getAllByText('Select All');
    fireEvent.click(selectAllBtns[selectAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
    (props.onChange as any).mockClear();
    const clearAllBtns = screen.getAllByText('Clear All');
    fireEvent.click(clearAllBtns[clearAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  it('percentile toggle: exercises toggleP inline fn', () => {
    const draft = baseFilterDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const { props } = renderFilterModal({ draft, savedDraft: draft });
    fireEvent.click(screen.getByText('Percentile'));
    fireEvent.click(screen.getByText('Top 1%'));
    expect(props.onChange).toHaveBeenCalled();
  });

  it('percentile Select All exercises selectAll fn', () => {
    const draft = baseFilterDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    draft.percentileFilter = { 0: false, 1: false };
    const { props } = renderFilterModal({ draft, savedDraft: draft });
    fireEvent.click(screen.getByText('Percentile'));
    const selectAllBtns = screen.getAllByText('Select All');
    fireEvent.click(selectAllBtns[selectAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  it('season toggle: exercises toggleSeason inline fn', () => {
    const draft = baseFilterDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const { props } = renderFilterModal({ draft, savedDraft: draft });
    fireEvent.click(screen.getByText('Season'));
    fireEvent.click(screen.getByText('Season 1'));
    expect(props.onChange).toHaveBeenCalled();
  });

  it('difficulty toggle: exercises toggleDiff inline fn', () => {
    const draft = baseFilterDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const { props } = renderFilterModal({ draft, savedDraft: draft });
    fireEvent.click(screen.getByText('Song Intensity'));
    const noScoreBtns = screen.getAllByText('No Score');
    fireEvent.click(noScoreBtns[noScoreBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  it('difficulty Select All + Clear All exercise bulk fns', () => {
    const draft = baseFilterDraft();
    draft.instrumentFilter = 'Solo_Guitar';
    const { props } = renderFilterModal({ draft, savedDraft: draft });
    fireEvent.click(screen.getByText('Song Intensity'));
    const selectAllBtns = screen.getAllByText('Select All');
    fireEvent.click(selectAllBtns[selectAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
    (props.onChange as any).mockClear();
    const clearAllBtns = screen.getAllByText('Clear All');
    fireEvent.click(clearAllBtns[clearAllBtns.length - 1]!);
    expect(props.onChange).toHaveBeenCalled();
  });

  it('enables Apply when savedDraft is undefined', () => {
    renderFilterModal({ savedDraft: undefined as unknown as FilterDraft });
    const btn = screen.getByText('Apply Filter Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });
});
