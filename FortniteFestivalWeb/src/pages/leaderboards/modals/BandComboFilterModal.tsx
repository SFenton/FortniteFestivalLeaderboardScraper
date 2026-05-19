/* eslint-disable react/forbid-dom-props -- notice uses inline modal style object */
import { useCallback, useEffect, useMemo, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { SERVER_INSTRUMENT_KEYS, type PlayerBandType, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { Border, Colors, Font, Gap, Radius, border, padding } from '@festival/theme';
import Modal from '../../../components/modals/Modal';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { areBandInstrumentDraftsEqual, BandInstrumentSlotSelector } from '../../band/modals/BandInstrumentFilterPicker';
import { bandTypeLabel } from '../../../utils/bandTypes';
import { bandComboIdFromInstruments } from '../../../utils/pageBandComboFilter';

type BandComboFilterModalProps = {
  visible: boolean;
  bandType: PlayerBandType;
  activeInstruments: readonly ServerInstrumentKey[];
  selectedBandName?: string;
  selectedBandHasGlobalFilter: boolean;
  onCancel: () => void;
  onApplyCombo: (comboId: string) => void;
  onClearCombo: () => void;
};

export default function BandComboFilterModal({
  visible,
  bandType,
  activeInstruments,
  selectedBandName,
  selectedBandHasGlobalFilter,
  onCancel,
  onApplyCombo,
  onClearCombo,
}: BandComboFilterModalProps) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const slotCount = getBandComboSlotCount(bandType);
  const savedDraft = useMemo(
    () => instrumentsToDraft(activeInstruments, slotCount),
    [activeInstruments, slotCount],
  );
  const [draft, setDraft] = useState<(ServerInstrumentKey | null)[]>(savedDraft);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const hasChanges = !areBandInstrumentDraftsEqual(draft, savedDraft);
  const draftClearsAppliedFilter = draft.every(instrument => instrument == null) && savedDraft.some(Boolean);
  const complete = draft.length === slotCount && draft.every(Boolean);
  const applyDisabled = !hasChanges || (!draftClearsAppliedFilter && !complete);
  const styles = useStyles();
  const selectedNotice = selectedBandName
    ? selectedBandHasGlobalFilter
      ? t('bandComboFilter.globalFilterNoticeSet', { band: selectedBandName })
      : t('bandComboFilter.globalFilterNoticeUnset', { band: selectedBandName })
    : null;

  useEffect(() => {
    if (!visible) return;
    setDraft(savedDraft);
    setConfirmOpen(false);
  }, [savedDraft, visible]);

  const handleClose = useCallback(() => {
    if (hasChanges) {
      setConfirmOpen(true);
      return;
    }
    onCancel();
  }, [hasChanges, onCancel]);

  const handleApply = useCallback(() => {
    if (applyDisabled) return;
    if (draftClearsAppliedFilter) {
      onClearCombo();
      return;
    }
    const selected = draft.filter((instrument): instrument is ServerInstrumentKey => !!instrument);
    if (selected.length !== slotCount) return;
    onApplyCombo(bandComboIdFromInstruments(selected));
  }, [applyDisabled, draft, draftClearsAppliedFilter, onApplyCombo, onClearCombo, slotCount]);

  const handleSelect = useCallback((index: number, instrument: ServerInstrumentKey | null) => {
    setDraft(current => replaceAt(current, index, instrument));
  }, []);

  const resetDraft = useCallback(() => {
    setDraft(emptyDraft(slotCount));
    setConfirmOpen(false);
  }, [slotCount]);

  const bandLabel = bandTypeLabel(bandType, t);

  return (
    <Modal
      visible={visible}
      title={t('bandComboFilter.modalTitle', { band: bandLabel })}
      onClose={handleClose}
      onApply={handleApply}
      onReset={resetDraft}
      resetLabel={t('bandComboFilter.clearTitle')}
      resetHint={t('bandComboFilter.clearHint')}
      applyDisabled={applyDisabled}
      afterPanel={confirmOpen ? (
        <ConfirmAlert
          title={t('bandComboFilter.cancelTitle')}
          message={t('bandComboFilter.cancelMessage')}
          onNo={() => setConfirmOpen(false)}
          onYes={onCancel}
          onExitComplete={() => setConfirmOpen(false)}
        />
      ) : null}
    >
      {selectedNotice && <div style={styles.notice}>{selectedNotice}</div>}
      {draft.map((instrument, index) => (
        <BandInstrumentSlotSelector
          key={index}
          index={index}
          selected={instrument}
          onSelect={(nextInstrument) => handleSelect(index, nextInstrument)}
          availableInstruments={SERVER_INSTRUMENT_KEYS}
          compact={isMobile}
        />
      ))}
    </Modal>
  );
}

function getBandComboSlotCount(bandType: PlayerBandType) {
  switch (bandType) {
    case 'Band_Duets': return 2;
    case 'Band_Trios': return 3;
    case 'Band_Quad': return 4;
  }
}

function instrumentsToDraft(instruments: readonly ServerInstrumentKey[], slotCount: number): (ServerInstrumentKey | null)[] {
  return Array.from({ length: slotCount }, (_, index) => instruments[index] ?? null);
}

function emptyDraft(slotCount: number): (ServerInstrumentKey | null)[] {
  return Array.from({ length: slotCount }, () => null);
}

function replaceAt<T>(items: readonly T[], index: number, value: T): T[] {
  return items.map((item, itemIndex) => itemIndex === index ? value : item);
}

function useStyles() {
  return useMemo(() => ({
    notice: {
      marginBottom: Gap.section,
      padding: padding(Gap.md, Gap.lg),
      borderRadius: Radius.sm,
      border: border(Border.thin, Colors.borderSubtle),
      backgroundColor: Colors.surfaceSubtle,
      color: Colors.textSecondary,
      fontSize: Font.sm,
      lineHeight: 1.4,
    } as CSSProperties,
  }), []);
}