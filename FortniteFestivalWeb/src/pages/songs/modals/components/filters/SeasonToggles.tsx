import { useTranslation } from 'react-i18next';
import { ToggleRow } from '../../../../../components/common/ToggleRow';
import { BulkActions } from '../../../../../components/modals/components/BulkActions';
import { useAvailableSeasons } from '../../../../../hooks/data/useAvailableSeasons';

interface SeasonTogglesProps {
  seasonFilter: Record<number, boolean>;
  onChange: (f: Record<number, boolean>) => void;
}

export function SeasonToggles({ seasonFilter, onChange }: SeasonTogglesProps) {
  const { t } = useTranslation();
  const availableSeasons = useAvailableSeasons();
  const seasons = [...availableSeasons, 0];

  const isOn = (s: number) => seasonFilter[s] !== false;
  const toggleSeason = (s: number) => onChange({ ...seasonFilter, [s]: !isOn(s) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of seasons) next[s] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of seasons) next[s] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {seasons.map(s => (
        <ToggleRow key={s} label={s === 0 ? t('format.noScore') : t('filter.seasonLabel', {season: s})} checked={isOn(s)} onToggle={() => toggleSeason(s)} />
      ))}
    </>
  );
}
