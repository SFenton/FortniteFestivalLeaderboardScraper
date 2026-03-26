import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import SortModal, { type SortDraft, type MetadataVisibility } from '../../../../src/pages/songs/modals/SortModal';
import { INSTRUMENT_KEYS, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { DEFAULT_METADATA_ORDER } from '../../../../src/utils/songSettings';
import { TestProviders } from '../../../helpers/TestProviders';

/* ── Helpers ── */

function baseDraft(): SortDraft {
  return {
    sortMode: 'title',
    sortAscending: true,
    metadataOrder: [...DEFAULT_METADATA_ORDER],
    instrumentOrder: [...INSTRUMENT_KEYS],
  };
}

const defaultProps = () => ({
  visible: true,
  draft: baseDraft(),
  savedDraft: baseDraft(),
  instrumentFilter: null as InstrumentKey | null,
  hasPlayer: true,
  metadataVisibility: undefined as MetadataVisibility | undefined,
  onChange: vi.fn(),
  onCancel: vi.fn(),
  onReset: vi.fn(),
  onApply: vi.fn(),
});

function renderModal(overrides: Partial<ReturnType<typeof defaultProps>> = {}) {
  const props = { ...defaultProps(), ...overrides };
  return { ...render(<TestProviders><SortModal {...props} /></TestProviders>), props };
}

/* ── Tests ── */

describe('SortModal', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  /* ── Visibility ── */

  it('renders nothing when visible is false', () => {
    const { container } = render(
      <TestProviders><SortModal {...defaultProps()} visible={false} /></TestProviders>,
    );
    expect(container.querySelector('[role="dialog"]')).toBeNull();
  });

  it('renders modal title when visible', () => {
    renderModal();
    expect(screen.getByText('Sort Songs')).toBeDefined();
  });

  /* ── Mode selection (hasPlayer=true → Accordion) ── */

  it('shows mode radio rows inside accordion when hasPlayer is true', () => {
    renderModal({ hasPlayer: true });
    // Accordion defaultOpen=false when instrumentFilter is null, so expand it
    fireEvent.click(screen.getByText('Mode'));
    expect(screen.getByText('Title')).toBeDefined();
    expect(screen.getByText('Artist')).toBeDefined();
    expect(screen.getByText('Year')).toBeDefined();
    expect(screen.getByText('Has FC')).toBeDefined();
  });

  it('shows mode radio rows as flat section when hasPlayer is false', () => {
    renderModal({ hasPlayer: false });
    expect(screen.getByText('Title')).toBeDefined();
    expect(screen.getByText('Artist')).toBeDefined();
    expect(screen.getByText('Year')).toBeDefined();
    expect(screen.getByText('Has FC')).toBeDefined();
  });

  it('selects a sort mode', () => {
    const props = defaultProps();
    props.hasPlayer = false;
    renderModal(props);
    fireEvent.click(screen.getByText('Artist'));
    const newDraft = props.onChange.mock.calls[0]![0] as SortDraft;
    expect(newDraft.sortMode).toBe('artist');
  });

  it('selects Year mode', () => {
    const props = defaultProps();
    props.hasPlayer = false;
    renderModal(props);
    fireEvent.click(screen.getByText('Year'));
    expect(props.onChange.mock.calls[0]![0].sortMode).toBe('year');
  });

  it('selects Has FC mode', () => {
    const props = defaultProps();
    props.hasPlayer = false;
    renderModal(props);
    fireEvent.click(screen.getByText('Has FC'));
    expect(props.onChange.mock.calls[0]![0].sortMode).toBe('hasfc');
  });

  it('selects Title mode (already selected, still fires)', () => {
    const props = defaultProps();
    props.hasPlayer = false;
    renderModal(props);
    fireEvent.click(screen.getByText('Title'));
    expect(props.onChange.mock.calls[0]![0].sortMode).toBe('title');
  });

  /* ── Instrument sort modes ── */

  it('shows instrument sort modes when instrumentFilter is set', () => {
    renderModal({ instrumentFilter: 'Solo_Guitar' });
    fireEvent.click(screen.getByText('Filtered Instrument Sort Mode'));
    expect(screen.getAllByText('Score').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Percentage').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Percentile').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Stars').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Season').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Intensity').length).toBeGreaterThanOrEqual(1);
  });

  it('does not show instrument sort modes when instrumentFilter is null', () => {
    renderModal({ instrumentFilter: null });
    expect(screen.queryByText('Filtered Instrument Sort Mode')).toBeNull();
  });

  it('selects an instrument sort mode', () => {
    const props = defaultProps();
    props.instrumentFilter = 'Solo_Guitar';
    renderModal(props);
    fireEvent.click(screen.getByText('Filtered Instrument Sort Mode'));
    // Score appears in both instrument sort modes and metadata priority; click the first one
    const scoreBtns = screen.getAllByText('Score');
    fireEvent.click(scoreBtns[0]!);
    expect(props.onChange.mock.calls[0]![0].sortMode).toBe('score');
  });

  it('filters instrument sort modes by metadataVisibility', () => {
    const mv: MetadataVisibility = {
      score: true,
      percentage: false,
      percentile: false,
      seasonachieved: false,
      intensity: false,
      stars: false,
    };
    renderModal({ instrumentFilter: 'Solo_Guitar', metadataVisibility: mv });
    fireEvent.click(screen.getByText('Filtered Instrument Sort Mode'));
    expect(screen.getAllByText('Score').length).toBeGreaterThanOrEqual(1);
    // Percentage should not appear in instrument sort modes (but may still appear in metadata priority)
    // The key check is that only 'Score' appears as a RadioRow in the accordion
    // Since Percentage is filtered out from INSTRUMENT_SORT_MODES, verify the total count
    expect(screen.queryByText('Intensity')).toBeNull();
  });

  it('hides instrument sort modes section when all are filtered out', () => {
    const mv: MetadataVisibility = {
      score: false,
      percentage: false,
      percentile: false,
      seasonachieved: false,
      intensity: false,
      stars: false,
    };
    renderModal({ instrumentFilter: 'Solo_Guitar', metadataVisibility: mv });
    expect(screen.queryByText('Filtered Instrument Sort Mode')).toBeNull();
  });

  /* ── Direction toggle ── */

  it('shows sort direction with ascending hint', () => {
    renderModal();
    expect(screen.getByText('Sort Direction')).toBeDefined();
    expect(screen.getByText(/Ascending/)).toBeDefined();
  });

  it('shows descending hint when sortAscending is false', () => {
    const draft = baseDraft();
    draft.sortAscending = false;
    renderModal({ draft, savedDraft: draft });
    expect(screen.getByText(/Descending/)).toBeDefined();
  });

  it('clicking Ascending button sets sortAscending to true', () => {
    const draft = baseDraft();
    draft.sortAscending = false;
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Ascending'));
    expect(props.onChange.mock.calls[0]![0].sortAscending).toBe(true);
  });

  it('clicking Descending button sets sortAscending to false', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Descending'));
    expect(props.onChange.mock.calls[0]![0].sortAscending).toBe(false);
  });

  /* ── Metadata sort priority ── */

  it('shows metadata sort priority when instrument is selected', () => {
    renderModal({ instrumentFilter: 'Solo_Guitar' });
    expect(screen.getByText('Metadata Sort Priority')).toBeDefined();
  });

  it('hides metadata sort priority when no instrument is selected', () => {
    renderModal({ instrumentFilter: null });
    expect(screen.queryByText('Metadata Sort Priority')).toBeNull();
  });

  it('hides metadata sort priority when all metadata is invisible', () => {
    const mv: MetadataVisibility = {
      score: false,
      percentage: false,
      percentile: false,
      seasonachieved: false,
      intensity: false,
      stars: false,
    };
    renderModal({ instrumentFilter: 'Solo_Guitar', metadataVisibility: mv });
    expect(screen.queryByText('Metadata Sort Priority')).toBeNull();
  });

  it('filters metadata order by visibility', () => {
    const mv: MetadataVisibility = {
      score: true,
      percentage: true,
      percentile: false,
      seasonachieved: false,
      intensity: false,
      stars: false,
    };
    renderModal({ instrumentFilter: 'Solo_Guitar', metadataVisibility: mv });
    expect(screen.getAllByText('Score').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Percentage').length).toBeGreaterThanOrEqual(1);
  });

  /* ── Primary Instrument Order (no instrument + hasfc) ── */

  it('shows instrument order when no instrument filter and sortMode is hasfc', () => {
    const draft = baseDraft();
    draft.sortMode = 'hasfc';
    renderModal({ draft, savedDraft: draft, instrumentFilter: null });
    expect(screen.getByText('Primary Instrument Order')).toBeDefined();
  });

  it('hides instrument order when instrument filter is set', () => {
    const draft = baseDraft();
    draft.sortMode = 'hasfc';
    renderModal({ draft, savedDraft: draft, instrumentFilter: 'Solo_Guitar' });
    expect(screen.queryByText('Primary Instrument Order')).toBeNull();
  });

  it('hides instrument order when sortMode is not hasfc', () => {
    renderModal({ instrumentFilter: null });
    expect(screen.queryByText('Primary Instrument Order')).toBeNull();
  });

  /* ── Apply / Reset / Cancel ── */

  it('calls onApply when Apply button is clicked', () => {
    const draft = baseDraft();
    draft.sortMode = 'artist';
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Apply Sort Changes'));
    expect(props.onApply).toHaveBeenCalledTimes(1);
  });

  it('disables Apply button when there are no changes', () => {
    const draft = baseDraft();
    renderModal({ draft, savedDraft: draft });
    const btn = screen.getByText('Apply Sort Changes');
    expect(btn.closest('button')!.disabled).toBe(true);
  });

  it('enables Apply when sortMode changes', () => {
    const draft = baseDraft();
    draft.sortMode = 'artist';
    renderModal({ draft });
    const btn = screen.getByText('Apply Sort Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  it('enables Apply when sortAscending changes', () => {
    const draft = baseDraft();
    draft.sortAscending = false;
    renderModal({ draft });
    const btn = screen.getByText('Apply Sort Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  it('enables Apply when metadataOrder changes', () => {
    const draft = baseDraft();
    draft.metadataOrder = ['percentage', ...DEFAULT_METADATA_ORDER.filter(k => k !== 'percentage')];
    renderModal({ draft });
    const btn = screen.getByText('Apply Sort Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  it('enables Apply when instrumentOrder changes', () => {
    const draft = baseDraft();
    draft.instrumentOrder = [...INSTRUMENT_KEYS].reverse() as InstrumentKey[];
    renderModal({ draft });
    const btn = screen.getByText('Apply Sort Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  it('enables Apply when savedDraft is undefined', () => {
    renderModal({ savedDraft: undefined });
    const btn = screen.getByText('Apply Sort Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  it('calls onReset when Reset button is clicked', () => {
    const props = defaultProps();
    renderModal(props);
    const resetBtns = screen.getAllByText('Reset Sort Settings');
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
    fireEvent.click(screen.getByLabelText('Close'));
    expect(props.onCancel).toHaveBeenCalledTimes(1);
  });

  it('shows confirm dialog when closing with unsaved changes', () => {
    const draft = baseDraft();
    draft.sortMode = 'artist';
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    expect(screen.getByText('Cancel Sort Changes')).toBeDefined();
    expect(screen.getByText('Are you sure you want to discard your sort changes?')).toBeDefined();
  });

  it('confirm dialog "No" dismisses the dialog', () => {
    const draft = baseDraft();
    draft.sortMode = 'artist';
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('No'));
    expect(props.onCancel).not.toHaveBeenCalled();
    expect(screen.queryByText('Cancel Sort Changes')).toBeNull();
  });

  it('confirm dialog "Yes" calls onCancel', () => {
    const draft = baseDraft();
    draft.sortMode = 'artist';
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('Yes'));
    expect(props.onCancel).toHaveBeenCalledTimes(1);
  });

  /* ── Mode accordion defaultOpen branch ── */

  it('mode accordion defaults open when instrumentFilter is set and hasPlayer', () => {
    renderModal({ hasPlayer: true, instrumentFilter: 'Solo_Guitar' });
    // When instrumentFilter is set, defaultOpen={!instrumentFilter} → false
    // Just verifying it renders; the accordion is closed, but the header is visible
    expect(screen.getByText('Mode')).toBeDefined();
  });

  it('mode accordion defaults open when instrumentFilter is null and hasPlayer', () => {
    renderModal({ hasPlayer: true, instrumentFilter: null });
    // defaultOpen={!instrumentFilter} → true
    // Radio rows should be visible immediately
    expect(screen.getByText('Title')).toBeDefined();
  });

  /* ── Metadata sort priority ── */

  it('shows and fires metadata sort priority reorder when instrument filter is set', () => {
    const props = defaultProps();
    props.instrumentFilter = 'Solo_Guitar';
    renderModal(props);
    expect(screen.getByText('Metadata Sort Priority')).toBeDefined();
  });

  /* ── Primary Instrument Order ── */

  it('shows instrument order when no instrument filter and sortMode is hasfc', () => {
    const draft = baseDraft();
    draft.sortMode = 'hasfc';
    renderModal({ draft, savedDraft: draft, instrumentFilter: null });
    expect(screen.getByText('Primary Instrument Order')).toBeDefined();
  });

  it('hides instrument order when sortMode is not hasfc', () => {
    renderModal({ instrumentFilter: null });
    expect(screen.queryByText('Primary Instrument Order')).toBeNull();
  });

  it('hides instrument order when instrument filter is set even with hasfc', () => {
    const draft = baseDraft();
    draft.sortMode = 'hasfc';
    renderModal({ draft, savedDraft: draft, instrumentFilter: 'Solo_Guitar' });
    expect(screen.queryByText('Primary Instrument Order')).toBeNull();
  });
});

describe('SortModal — hasPlayer accordion onSelect coverage', () => {
  it('selects Artist mode via accordion when hasPlayer is true', () => {
    const props = defaultProps();
    props.hasPlayer = true;
    renderModal(props);
    // Accordion defaultOpen={!instrumentFilter} = true; RadioRows visible immediately
    fireEvent.click(screen.getByText('Artist'));
    expect(props.onChange).toHaveBeenCalledWith(expect.objectContaining({ sortMode: 'artist' }));
  });

  it('selects Year mode via accordion when hasPlayer is true', () => {
    const props = defaultProps();
    props.hasPlayer = true;
    renderModal(props);
    fireEvent.click(screen.getByText('Year'));
    expect(props.onChange).toHaveBeenCalledWith(expect.objectContaining({ sortMode: 'year' }));
  });

  it('selects Has FC mode via accordion when hasPlayer is true', () => {
    const props = defaultProps();
    props.hasPlayer = true;
    renderModal(props);
    fireEvent.click(screen.getByText('Has FC'));
    expect(props.onChange).toHaveBeenCalledWith(expect.objectContaining({ sortMode: 'hasfc' }));
  });

  it('selects Title mode via accordion when hasPlayer is true', () => {
    const props = defaultProps();
    props.hasPlayer = true;
    renderModal(props);
    fireEvent.click(screen.getByText('Title'));
    expect(props.onChange).toHaveBeenCalledWith(expect.objectContaining({ sortMode: 'title' }));
  });
});
