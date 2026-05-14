import { useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import type { SelectedBandProfile } from '../../../hooks/data/useSelectedProfile';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { useModalDraft } from '../../../hooks/ui/useModalDraft';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import Modal from '../../../components/modals/Modal';
import type { BandInstrumentFilterApplyPayload, BandInstrumentFilterAssignment } from '../../../types/bandFilter';
import {
  areBandInstrumentDraftsEqual,
  BandInstrumentFilterInvalidSelectionAlert,
  BandInstrumentFilterPicker,
  useBandInstrumentFilterController,
} from './BandInstrumentFilterPicker';

export type { BandInstrumentFilterApplyPayload, BandInstrumentFilterAssignment } from '../../../types/bandFilter';

type BandInstrumentFilterModalProps = {
  visible: boolean;
  selectedBand: SelectedBandProfile | null;
  appliedAssignments: readonly BandInstrumentFilterAssignment[];
  onCancel: () => void;
  onApply: (payload: BandInstrumentFilterApplyPayload) => void;
  onReset: () => void;
};

export default function BandInstrumentFilterModal({
  visible,
  selectedBand,
  appliedAssignments,
  onCancel,
  onApply,
  onReset,
}: BandInstrumentFilterModalProps) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const controller = useBandInstrumentFilterController({
    visible,
    selectedBand,
    appliedAssignments,
    onApply,
    onReset,
  });
  const { hasChanges, confirmOpen, setConfirmOpen, handleClose } = useModalDraft(
    controller.draft,
    controller.savedDraft,
    onCancel,
    areBandInstrumentDraftsEqual,
  );
  const resetDraft = useCallback(() => {
    controller.resetDraft();
    setConfirmOpen(false);
  }, [controller, setConfirmOpen]);

  return (
    <Modal
      visible={visible}
      title={t('bandFilter.modalTitle')}
      onClose={handleClose}
      onApply={controller.apply}
      onReset={resetDraft}
      resetLabel={t('bandFilter.resetTitle')}
      resetHint={t('bandFilter.resetHint')}
      applyDisabled={controller.applyDisabled || !hasChanges}
      afterPanel={(
        controller.pendingInvalidSelection ? (
          <BandInstrumentFilterInvalidSelectionAlert controller={controller} />
        ) : confirmOpen ? (
          <ConfirmAlert
            title={t('bandFilter.cancelTitle')}
            message={t('bandFilter.cancelMessage')}
            onNo={() => setConfirmOpen(false)}
            onYes={onCancel}
            onExitComplete={() => setConfirmOpen(false)}
          />
        ) : null
      )}
    >
      <BandInstrumentFilterPicker controller={controller} compact={isMobile} />
    </Modal>
  );
}
