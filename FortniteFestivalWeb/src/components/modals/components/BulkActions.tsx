import { useTranslation } from 'react-i18next';
import { modalStyles } from '../modalStyles';

export interface BulkActionsProps {
  onSelectAll: () => void;
  onClearAll: () => void;
}

export function BulkActions({ onSelectAll, onClearAll }: BulkActionsProps) {
  const { t } = useTranslation();
  return (
    <div style={modalStyles.bulkWrap}>
      <button style={modalStyles.bulkSelectBtn} onClick={onSelectAll}>{t('common.selectAll')}</button>
      <button style={modalStyles.bulkClearBtn} onClick={onClearAll}>{t('common.clearAll')}</button>
    </div>
  );
}
