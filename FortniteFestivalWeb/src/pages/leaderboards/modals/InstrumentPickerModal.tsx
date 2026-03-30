/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useMemo, useCallback } from 'react';
import Modal from '../../../components/modals/Modal';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import { InstrumentSelector, type InstrumentSelectorItem } from '../../../components/common/InstrumentSelector';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { INSTRUMENT_KEYS, INSTRUMENT_LABELS } from '@festival/core/api/serverTypes';
import { useSettings, isInstrumentVisible } from '../../../contexts/SettingsContext';
import { useModalDraft } from '../../../hooks/ui/useModalDraft';
import { useTranslation } from 'react-i18next';

type InstrumentPickerModalProps = {
  visible: boolean;
  draft: InstrumentKey;
  savedDraft?: InstrumentKey;
  onChange: (key: InstrumentKey) => void;
  onCancel: () => void;
  onApply: () => void;
};

export default function InstrumentPickerModal({ visible, draft, savedDraft, onChange, onCancel, onApply }: InstrumentPickerModalProps) {
  const { t } = useTranslation();
  const { settings } = useSettings();

  const selectorItems = useMemo<InstrumentSelectorItem[]>(
    () => INSTRUMENT_KEYS.filter(k => isInstrumentVisible(settings, k)).map(key => ({ key, label: INSTRUMENT_LABELS[key] })),
    [settings],
  );

  const handleSelect = useCallback((key: InstrumentKey | null) => {
    if (key) onChange(key);
  }, [onChange]);

  const { hasChanges, confirmOpen, setConfirmOpen, handleClose } = useModalDraft(
    draft, savedDraft, onCancel, (a, b) => a === b,
  );

  return (
    <Modal
      visible={visible}
      title={t('rankings.changeInstrument')}
      onClose={handleClose}
      onApply={onApply}
      applyDisabled={!hasChanges}
      afterPanel={confirmOpen ? (
        <ConfirmAlert
          title={t('rankings.instrumentCancelTitle')}
          message={t('rankings.instrumentCancelMessage')}
          onNo={() => setConfirmOpen(false)}
          onYes={onCancel}
          onExitComplete={() => setConfirmOpen(false)}
        />
      ) : null}
    >
      <ModalSection title={t('rankings.changeInstrument')} hint={t('rankings.instrumentHint')}>
        <InstrumentSelector
          instruments={selectorItems}
          selected={draft}
          onSelect={handleSelect}
          required
        />
      </ModalSection>
    </Modal>
  );
}
