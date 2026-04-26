import { useTranslation } from 'react-i18next';
import type { PlayerBandListGroup } from '@festival/core/api/serverTypes';
import Modal from '../../../components/modals/Modal';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import { RadioRow } from '../../../components/common/RadioRow';
import { useModalDraft } from '../../../hooks/ui/useModalDraft';

export type BandFilterDraft = PlayerBandListGroup;

const BAND_GROUP_OPTIONS: PlayerBandListGroup[] = ['all', 'duos', 'trios', 'quads'];

type BandFilterModalProps = {
  visible: boolean;
  draft: BandFilterDraft;
  savedDraft: BandFilterDraft;
  onChange: (draft: BandFilterDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

export default function BandFilterModal({ visible, draft, savedDraft, onChange, onCancel, onReset, onApply }: BandFilterModalProps) {
  const { t } = useTranslation();
  const { hasChanges, confirmOpen, setConfirmOpen, handleClose } = useModalDraft(
    draft,
    savedDraft,
    onCancel,
    (a, b) => a === b,
  );

  return (
    <Modal
      visible={visible}
      title={t('bandList.filterTitle')}
      onClose={handleClose}
      onApply={onApply}
      onReset={onReset}
      resetLabel={t('bandList.filterReset')}
      resetHint={t('bandList.filterResetHint')}
      applyDisabled={!hasChanges}
      afterPanel={confirmOpen ? (
        <ConfirmAlert
          title={t('bandList.cancelTitle')}
          message={t('bandList.cancelMessage')}
          onNo={() => setConfirmOpen(false)}
          onYes={onCancel}
          onExitComplete={() => setConfirmOpen(false)}
        />
      ) : null}
    >
      <ModalSection title={t('bandList.groupTitle')} hint={t('bandList.groupHint')}>
        {BAND_GROUP_OPTIONS.map(group => (
          <RadioRow
            key={group}
            label={t(`bandList.groups.${group}`)}
            hint={t(`bandList.groupHints.${group}`)}
            selected={draft === group}
            onSelect={() => onChange(group)}
          />
        ))}
      </ModalSection>
    </Modal>
  );
}
