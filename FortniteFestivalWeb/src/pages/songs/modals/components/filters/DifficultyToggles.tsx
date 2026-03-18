import { useTranslation } from 'react-i18next';
import { ToggleRow } from '../../../../../components/common/ToggleRow';
import { BulkActions } from '../../../../../components/modals/components/BulkActions';
import DifficultyBars from '../../../../../components/songs/metadata/DifficultyBars';

interface DifficultyTogglesProps {
  difficultyFilter: Record<number, boolean>;
  onChange: (f: Record<number, boolean>) => void;
}

export function DifficultyToggles({ difficultyFilter, onChange }: DifficultyTogglesProps) {
  const { t } = useTranslation();
  const allKeys = [1, 2, 3, 4, 5, 6, 7, 0];
  const diffLabel = (k: number): React.ReactNode =>
    k === 0 ? t('format.noScore') : <DifficultyBars level={k} />;

  const isOn = (d: number) => difficultyFilter[d] !== false;
  const toggleD = (d: number) => onChange({ ...difficultyFilter, [d]: !isOn(d) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const d of allKeys) next[d] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const d of allKeys) next[d] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {allKeys.map(d => (
        <ToggleRow key={d} label={diffLabel(d)} checked={isOn(d)} onToggle={() => toggleD(d)} />
      ))}
    </>
  );
}
