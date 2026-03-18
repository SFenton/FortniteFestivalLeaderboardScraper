import { useTranslation } from 'react-i18next';
import css from '../Modal.module.css';

export interface BulkActionsProps {
  onSelectAll: () => void;
  onClearAll: () => void;
}

export function BulkActions({ onSelectAll, onClearAll }: BulkActionsProps) {
  const { t } = useTranslation();
  return (
    <div className={css.bulkWrap}>
      <button className={css.bulkSelectBtn} onClick={onSelectAll}>{t('common.selectAll')}</button>
      <button className={css.bulkClearBtn} onClick={onClearAll}>{t('common.clearAll')}</button>
    </div>
  );
}
