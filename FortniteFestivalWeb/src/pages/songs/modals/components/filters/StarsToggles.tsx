/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useTranslation } from 'react-i18next';
import { Gap, Size } from '@festival/theme';
import { ToggleRow } from '../../../../../components/common/ToggleRow';
import { BulkActions } from '../../../../../components/modals/components/BulkActions';

interface StarsTogglesProps {
  starsFilter: Record<number, boolean>;
  onChange: (f: Record<number, boolean>) => void;
}

export function StarsToggles({ starsFilter, onChange }: StarsTogglesProps) {
  const { t } = useTranslation();
  const allKeys = [6, 5, 4, 3, 2, 1, 0];

  const starLabel = (k: number) => {
    if (k === 0) return t('format.noScore');
    const isGold = k === 6;
    const count = isGold ? 5 : k;
    const src = isGold ? `${import.meta.env.BASE_URL}star_gold.png` : `${import.meta.env.BASE_URL}star_white.png`;
    return (
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: Gap.xs }}>
        {Array.from({ length: count }, (_, i) => (
          <img key={i} src={src} alt="" width={Size.starInline} height={Size.starInline} />
        ))}
      </span>
    );
  };

  const isOn = (s: number) => starsFilter[s] !== false;
  const toggleS = (s: number) => onChange({ ...starsFilter, [s]: !isOn(s) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of allKeys) next[s] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of allKeys) next[s] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {allKeys.map(s => (
        <ToggleRow key={s} label={starLabel(s)} checked={isOn(s)} onToggle={() => toggleS(s)} />
      ))}
    </>
  );
}
