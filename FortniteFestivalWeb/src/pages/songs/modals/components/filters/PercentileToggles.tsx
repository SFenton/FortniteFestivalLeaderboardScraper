import { useTranslation } from 'react-i18next';
import { PERCENTILE_THRESHOLDS } from '@festival/core';
import { ToggleRow } from '../../../../../components/common/ToggleRow';
import { BulkActions } from '../../../../../components/modals/components/BulkActions';

interface PercentileTogglesProps {
  percentileFilter: Record<number, boolean>;
  onChange: (f: Record<number, boolean>) => void;
}

export function PercentileToggles({ percentileFilter, onChange }: PercentileTogglesProps) {
  const { t } = useTranslation();
  const allKeys = [0, ...PERCENTILE_THRESHOLDS];
  const isOn = (p: number) => percentileFilter[p] !== false;
  const toggleP = (p: number) => onChange({ ...percentileFilter, [p]: !isOn(p) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const p of allKeys) next[p] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const p of allKeys) next[p] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {allKeys.map(p => (
        <ToggleRow key={p} label={p === 0 ? t('format.noScore') : t('format.topPercent', {value: p})} checked={isOn(p)} onToggle={() => toggleP(p)} />
      ))}
    </>
  );
}
