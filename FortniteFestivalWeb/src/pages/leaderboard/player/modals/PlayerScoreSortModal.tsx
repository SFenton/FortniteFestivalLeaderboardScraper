/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */

import { useTranslation } from 'react-i18next';
import Modal from '../../../../components/modals/Modal';
import { ModalSection } from '../../../../components/modals/components/ModalSection';
import { RadioRow } from '../../../../components/common/RadioRow';
import ConfirmAlert from '../../../../components/modals/ConfirmAlert';
import { Colors, Font, Gap, Size } from '@festival/theme';
import { IoArrowUp, IoArrowDown } from 'react-icons/io5';

export type PlayerScoreSortMode = 'date' | 'score' | 'accuracy' | 'season';

export type PlayerScoreSortDraft = {
  sortMode: PlayerScoreSortMode;
  sortAscending: boolean;
};

type PlayerScoreSortModalProps = {
  visible: boolean;
  draft: PlayerScoreSortDraft;
  savedDraft?: PlayerScoreSortDraft;
  onChange: (d: PlayerScoreSortDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

import { useModalDraft } from '../../../../hooks/ui/useModalDraft';

export default function PlayerScoreSortModal({ visible, draft, savedDraft, onChange, onCancel, onReset, onApply }: PlayerScoreSortModalProps) {
  const { t } = useTranslation();
  const setMode = (sortMode: PlayerScoreSortMode) => onChange({ ...draft, sortMode });

  const { hasChanges, confirmOpen, setConfirmOpen, handleClose } = useModalDraft(
    draft, savedDraft, onCancel,
    (a, b) => a.sortMode === b.sortMode && a.sortAscending === b.sortAscending,
  );

  return (
      <Modal
        visible={visible}
        title={t('common.sortPlayerScores')}
        onClose={handleClose}
        onApply={onApply}
        onReset={onReset}
        resetLabel={t('sort.resetLabel')}
        resetHint={t('sort.resetHint')}
        applyLabel={t('sort.applyLabel')}
        applyDisabled={!hasChanges}
        afterPanel={confirmOpen ? (
          <ConfirmAlert
            title={t('sort.cancelTitle')}
            message={t('sort.cancelMessage')}
            onNo={() => setConfirmOpen(false)}
            onYes={onCancel}
            onExitComplete={() => setConfirmOpen(false)}
          />
        ) : null}
      >
        <ModalSection title={t('sort.mode')} hint={t('sort.modeHint')}>
          <RadioRow label={t('sort.date')} selected={draft.sortMode === 'date'} onSelect={() => setMode('date')} />
          <RadioRow label={t('sort.score')} selected={draft.sortMode === 'score'} onSelect={() => setMode('score')} />
          <RadioRow label={t('sort.accuracy')} selected={draft.sortMode === 'accuracy'} onSelect={() => setMode('accuracy')} />
          <RadioRow label={t('sort.season')} selected={draft.sortMode === 'season'} onSelect={() => setMode('season')} />
        </ModalSection>

        <ModalSection>
          <div style={directionStyles.inner}>
            <div style={directionStyles.textCol}>
              <div style={directionStyles.title}>{t('sort.direction')}</div>
              <div style={directionStyles.hint}>
                {draft.sortAscending ? t('sort.ascendingHintScores') : t('sort.descendingHintScores')}
              </div>
            </div>
            <div style={directionStyles.icons}>
              <button
                style={directionStyles.iconBtn}
                onClick={() => onChange({ ...draft, sortAscending: true })}
                aria-label={t('aria.ascending')}
              >
                <div style={{ ...directionStyles.iconCircle, ...(draft.sortAscending ? directionStyles.iconCircleActive : {}) }} />
                <IoArrowUp size={Size.iconDefault} style={{ position: 'relative' as const, zIndex: 1, color: draft.sortAscending ? Colors.textPrimary : Colors.textMuted, transition: 'color 200ms ease' }} />
              </button>
              <button
                style={directionStyles.iconBtn}
                onClick={() => onChange({ ...draft, sortAscending: false })}
                aria-label={t('aria.descending')}
              >
                <div style={{ ...directionStyles.iconCircle, ...(!draft.sortAscending ? directionStyles.iconCircleActive : {}) }} />
                <IoArrowDown size={Size.iconDefault} style={{ position: 'relative' as const, zIndex: 1, color: !draft.sortAscending ? Colors.textPrimary : Colors.textMuted, transition: 'color 200ms ease' }} />
              </button>
            </div>
          </div>
        </ModalSection>
      </Modal>
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
