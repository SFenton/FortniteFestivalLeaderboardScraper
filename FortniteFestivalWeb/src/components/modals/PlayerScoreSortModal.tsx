import { useMemo, useCallback, useState } from 'react';
import Modal, { ModalSection, RadioRow } from './Modal';
import ConfirmAlert from './ConfirmAlert';
import { Colors, Font, Gap } from '@festival/theme';
import { IoArrowUp, IoArrowDown } from 'react-icons/io5';

export type PlayerScoreSortMode = 'date' | 'score' | 'accuracy' | 'season';

export type PlayerScoreSortDraft = {
  sortMode: PlayerScoreSortMode;
  sortAscending: boolean;
};

type Props = {
  visible: boolean;
  draft: PlayerScoreSortDraft;
  savedDraft?: PlayerScoreSortDraft;
  onChange: (d: PlayerScoreSortDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

export default function PlayerScoreSortModal({ visible, draft, savedDraft, onChange, onCancel, onReset, onApply }: Props) {
  const setMode = (sortMode: PlayerScoreSortMode) => onChange({ ...draft, sortMode });

  const hasChanges = useMemo(() => {
    if (!savedDraft) return true;
    return draft.sortMode !== savedDraft.sortMode || draft.sortAscending !== savedDraft.sortAscending;
  }, [draft, savedDraft]);

  const [confirmOpen, setConfirmOpen] = useState(false);
  const handleClose = useCallback(() => {
    if (hasChanges) setConfirmOpen(true);
    else onCancel();
  }, [hasChanges, onCancel]);
  const confirmDiscard = useCallback(() => {
    setConfirmOpen(false);
    onCancel();
  }, [onCancel]);

  return (
    <>
      <Modal
        visible={visible}
        title="Sort Player Scores"
        onClose={handleClose}
        onApply={onApply}
        onReset={onReset}
        resetLabel="Reset Sort Settings"
        resetHint="Restore sort mode and direction to their defaults."
        applyLabel="Apply Sort Changes"
        applyDisabled={!hasChanges}
      >
        <ModalSection title="Mode" hint="Choose which property to sort scores by.">
          <RadioRow label="Date" selected={draft.sortMode === 'date'} onSelect={() => setMode('date')} />
          <RadioRow label="Score" selected={draft.sortMode === 'score'} onSelect={() => setMode('score')} />
          <RadioRow label="Accuracy" selected={draft.sortMode === 'accuracy'} onSelect={() => setMode('accuracy')} />
          <RadioRow label="Season" selected={draft.sortMode === 'season'} onSelect={() => setMode('season')} />
        </ModalSection>

        <ModalSection>
          <div style={directionStyles.inner}>
            <div style={directionStyles.textCol}>
              <div style={directionStyles.title}>Sort Direction</div>
              <div style={directionStyles.hint}>
                {draft.sortAscending ? 'Ascending (oldest first, low–high)' : 'Descending (newest first, high–low)'}
              </div>
            </div>
            <div style={directionStyles.icons}>
              <button
                style={directionStyles.iconBtn}
                onClick={() => onChange({ ...draft, sortAscending: true })}
                aria-label="Ascending"
              >
                <div style={{ ...directionStyles.iconCircle, ...(draft.sortAscending ? directionStyles.iconCircleActive : {}) }} />
                <IoArrowUp size={20} style={{ position: 'relative' as const, zIndex: 1, color: draft.sortAscending ? Colors.textPrimary : Colors.textMuted, transition: 'color 200ms ease' }} />
              </button>
              <button
                style={directionStyles.iconBtn}
                onClick={() => onChange({ ...draft, sortAscending: false })}
                aria-label="Descending"
              >
                <div style={{ ...directionStyles.iconCircle, ...(!draft.sortAscending ? directionStyles.iconCircleActive : {}) }} />
                <IoArrowDown size={20} style={{ position: 'relative' as const, zIndex: 1, color: !draft.sortAscending ? Colors.textPrimary : Colors.textMuted, transition: 'color 200ms ease' }} />
              </button>
            </div>
          </div>
        </ModalSection>
      </Modal>

      {confirmOpen && (
        <ConfirmAlert
          title="Cancel Sort Changes"
          message="Are you sure you want to discard your sort changes?"
          onNo={() => setConfirmOpen(false)}
          onYes={confirmDiscard}
        />
      )}
    </>
  );
}

const directionStyles: Record<string, React.CSSProperties> = {
  inner: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    paddingBottom: Gap.md,
  },
  textCol: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
  },
  title: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.textPrimary,
  },
  hint: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  icons: {
    display: 'flex',
    gap: Gap.md,
    flexShrink: 0,
    marginRight: -12,
  },
  iconBtn: {
    width: 40,
    height: 40,
    borderRadius: '50%',
    border: 'none',
    backgroundColor: 'transparent',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    position: 'relative' as const,
    overflow: 'hidden' as const,
  },
  iconCircle: {
    position: 'absolute' as const,
    inset: 0,
    borderRadius: '50%',
    backgroundColor: Colors.accentPurple,
    transform: 'scale(0)',
    transition: 'transform 250ms ease',
  },
  iconCircleActive: {
    transform: 'scale(1)',
  },
};
