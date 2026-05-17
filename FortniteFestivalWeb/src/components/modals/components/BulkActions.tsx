import { useTranslation } from 'react-i18next';
import PressableButton from '../../common/PressableButton';
import { modalStyles } from '../modalStyles';

export interface BulkActionsProps {
  onSelectAll: () => void;
  onClearAll: () => void;
}

export function BulkActions({ onSelectAll, onClearAll }: BulkActionsProps) {
  const { t } = useTranslation();
  return (
    <div style={modalStyles.bulkWrap}>
      <PressableButton style={modalStyles.bulkSelectBtn} onPress={onSelectAll}>{t('common.selectAll')}</PressableButton>
      <PressableButton style={modalStyles.bulkClearBtn} onPress={onClearAll}>{t('common.clearAll')}</PressableButton>
    </div>
  );
}
