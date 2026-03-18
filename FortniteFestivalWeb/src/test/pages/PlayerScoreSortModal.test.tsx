import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import PlayerScoreSortModal, {
  type PlayerScoreSortDraft,
  type PlayerScoreSortMode,
} from '../../pages/leaderboard/player/modals/PlayerScoreSortModal';
import { TestProviders } from '../helpers/TestProviders';

/* ── Helpers ── */

function baseDraft(): PlayerScoreSortDraft {
  return { sortMode: 'date', sortAscending: false };
}

const defaultProps = () => ({
  visible: true,
  draft: baseDraft(),
  savedDraft: baseDraft(),
  onChange: vi.fn(),
  onCancel: vi.fn(),
  onReset: vi.fn(),
  onApply: vi.fn(),
});

function renderModal(overrides: Partial<ReturnType<typeof defaultProps>> = {}) {
  const props = { ...defaultProps(), ...overrides };
  return { ...render(<TestProviders><PlayerScoreSortModal {...props} /></TestProviders>), props };
}

/* ── Tests ── */

describe('PlayerScoreSortModal', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  /* ── Visibility ── */

  it('renders nothing when visible is false', () => {
    const { container } = render(
      <TestProviders><PlayerScoreSortModal {...defaultProps()} visible={false} /></TestProviders>,
    );
    expect(container.innerHTML).toBe('');
  });

  it('renders modal title when visible', () => {
    renderModal();
    // i18n returns 'Sort Player Scores' for 'common.sortPlayerScores'
    expect(screen.getByText('Sort Player Scores')).toBeDefined();
  });

  /* ── Mode selection ── */

  it('shows all four sort mode radio rows', () => {
    renderModal();
    expect(screen.getByText('Date')).toBeDefined();
    expect(screen.getByText('Score')).toBeDefined();
    expect(screen.getByText('Accuracy')).toBeDefined();
    expect(screen.getByText('Season')).toBeDefined();
  });

  it('selects date mode', () => {
    const draft = baseDraft();
    draft.sortMode = 'score';
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByText('Date'));
    expect(props.onChange.mock.calls[0]![0].sortMode).toBe('date');
  });

  it('selects score mode', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Score'));
    expect(props.onChange.mock.calls[0]![0].sortMode).toBe('score');
  });

  it('selects accuracy mode', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Accuracy'));
    expect(props.onChange.mock.calls[0]![0].sortMode).toBe('accuracy');
  });

  it('selects season mode', () => {
    const props = defaultProps();
    renderModal(props);
    fireEvent.click(screen.getByText('Season'));
    expect(props.onChange.mock.calls[0]![0].sortMode).toBe('season');
  });

  /* ── Direction toggle ── */

  it('shows sort direction section', () => {
    renderModal();
    // i18n returns 'Sort Direction' for 'sort.direction'
    expect(screen.getByText('Sort Direction')).toBeDefined();
  });

  it('shows descending hint when sortAscending is false', () => {
    renderModal({ draft: { sortMode: 'date', sortAscending: false } });
    // i18n: 'Descending (newest first, high–low)'
    expect(screen.getByText(/Descending/)).toBeDefined();
  });

  it('shows ascending hint when sortAscending is true', () => {
    renderModal({ draft: { sortMode: 'date', sortAscending: true }, savedDraft: { sortMode: 'date', sortAscending: true } });
    // i18n: 'Ascending (oldest first, low–high)'
    expect(screen.getByText(/Ascending/)).toBeDefined();
  });

  it('clicking Ascending button sets sortAscending to true', () => {
    const draft: PlayerScoreSortDraft = { sortMode: 'date', sortAscending: false };
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    // i18n: aria.ascending = 'Ascending'
    fireEvent.click(screen.getByLabelText('Ascending'));
    expect(props.onChange.mock.calls[0]![0].sortAscending).toBe(true);
  });

  it('clicking Descending button sets sortAscending to false', () => {
    const draft: PlayerScoreSortDraft = { sortMode: 'date', sortAscending: true };
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Descending'));
    expect(props.onChange.mock.calls[0]![0].sortAscending).toBe(false);
  });

  it('preserves sortMode when toggling direction', () => {
    const draft: PlayerScoreSortDraft = { sortMode: 'accuracy', sortAscending: true };
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Descending'));
    expect(props.onChange.mock.calls[0]![0].sortMode).toBe('accuracy');
    expect(props.onChange.mock.calls[0]![0].sortAscending).toBe(false);
  });

  /* ── Apply / Reset / Cancel ── */

  it('calls onApply when Apply button is clicked', () => {
    const draft: PlayerScoreSortDraft = { sortMode: 'score', sortAscending: false };
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    // i18n: sort.applyLabel = 'Apply Sort Changes'
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
    const draft: PlayerScoreSortDraft = { sortMode: 'score', sortAscending: false };
    const saved: PlayerScoreSortDraft = { sortMode: 'date', sortAscending: false };
    renderModal({ draft, savedDraft: saved });
    const btn = screen.getByText('Apply Sort Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });

  it('enables Apply when sortAscending changes', () => {
    const draft: PlayerScoreSortDraft = { sortMode: 'date', sortAscending: true };
    const saved: PlayerScoreSortDraft = { sortMode: 'date', sortAscending: false };
    renderModal({ draft, savedDraft: saved });
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
    // i18n: sort.resetLabel = 'Reset Sort Settings'
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
    const draft: PlayerScoreSortDraft = { sortMode: 'score', sortAscending: false };
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    // i18n: sort.cancelTitle = 'Cancel Sort Changes'
    expect(screen.getByText('Cancel Sort Changes')).toBeDefined();
    // i18n: sort.cancelMessage = 'Are you sure you want to discard your sort changes?'
    expect(screen.getByText('Are you sure you want to discard your sort changes?')).toBeDefined();
  });

  it('confirm dialog "No" dismisses the dialog', () => {
    const draft: PlayerScoreSortDraft = { sortMode: 'score', sortAscending: false };
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('No'));
    expect(props.onCancel).not.toHaveBeenCalled();
    expect(screen.queryByText('Cancel Sort Changes')).toBeNull();
  });

  it('confirm dialog "Yes" calls onCancel', () => {
    const draft: PlayerScoreSortDraft = { sortMode: 'score', sortAscending: false };
    const props = defaultProps();
    props.draft = draft;
    renderModal(props);
    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByText('Yes'));
    expect(props.onCancel).toHaveBeenCalledTimes(1);
  });

  /* ── All sort modes covered by hasChanges ── */

  it.each<[PlayerScoreSortMode, PlayerScoreSortMode]>([
    ['date', 'score'],
    ['date', 'accuracy'],
    ['date', 'season'],
    ['score', 'date'],
    ['accuracy', 'date'],
    ['season', 'date'],
  ])('hasChanges detects mode change %s → %s', (from, to) => {
    const draft: PlayerScoreSortDraft = { sortMode: to, sortAscending: false };
    const saved: PlayerScoreSortDraft = { sortMode: from, sortAscending: false };
    renderModal({ draft, savedDraft: saved });
    const btn = screen.getByText('Apply Sort Changes');
    expect(btn.closest('button')!.disabled).toBe(false);
  });
});
