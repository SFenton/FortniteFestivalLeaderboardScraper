import { useTranslation } from 'react-i18next';
import type { CSSProperties } from 'react';
import Modal from '../../../components/modals/Modal';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import { RadioRow } from '../../../components/common/RadioRow';
import { DirectionSelector } from '../../../components/common/DirectionSelector';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import { useModalDraft } from '../../../hooks/ui/useModalDraft';
import { Gap } from '@festival/theme';
import { SHOP_SORT_MODES, type ShopSortMode, type ShopSortSettings } from '../shopSort';

const MODE_LABEL_KEYS: Record<ShopSortMode, string> = {
  title: 'sort.title',
  artist: 'sort.artist',
  year: 'sort.year',
  duration: 'sort.duration',
};

/* Matches the Songs sort modal's direction row spacing. */
const directionWrapStyle: CSSProperties = { paddingBottom: Gap.md, marginRight: -Gap.md };

type ShopSortModalProps = {
  visible: boolean;
  draft: ShopSortSettings;
  savedDraft?: ShopSortSettings;
  onChange: (d: ShopSortSettings) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

export default function ShopSortModal({ visible, draft, savedDraft, onChange, onCancel, onReset, onApply }: ShopSortModalProps) {
  const { t } = useTranslation();

  const { hasChanges, confirmOpen, setConfirmOpen, handleClose } = useModalDraft(
    draft, savedDraft, onCancel,
    (a, b) => a.sortMode === b.sortMode && a.sortAscending === b.sortAscending,
  );

  return (
    <Modal
      visible={visible}
      title={t('shop.sortTitle')}
      onClose={handleClose}
      onApply={onApply}
      onReset={onReset}
      resetLabel={t('sort.resetLabel')}
      resetHint={t('shop.sortResetHint')}
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
      <ModalSection title={t('sort.mode')} hint={t('shop.sortModeHint')}>
        {SHOP_SORT_MODES.map(mode => (
          <RadioRow
            key={mode}
            label={t(MODE_LABEL_KEYS[mode])}
            selected={draft.sortMode === mode}
            onSelect={() => onChange({ ...draft, sortMode: mode })}
          />
        ))}
      </ModalSection>

      <ModalSection>
        <div style={directionWrapStyle}>
          <DirectionSelector
            ascending={draft.sortAscending}
            onChange={sortAscending => onChange({ ...draft, sortAscending })}
            ascendingLabel={t('aria.ascending')}
            descendingLabel={t('aria.descending')}
          />
        </div>
      </ModalSection>
    </Modal>
  );
}
