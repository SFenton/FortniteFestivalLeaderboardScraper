import { memo, useContext } from 'react';
import { FestivalContext } from '../../../contexts/FestivalContext';
import css from './SeasonPill.module.css';

export default memo(function SeasonPill({ season, current }: { season: number; current?: boolean }) {
  const ctx = useContext(FestivalContext);
  const currentSeason = ctx?.state.currentSeason ?? 0;
  const isCurrent = current ?? (currentSeason > 0 && season === currentSeason);
  return <span className={isCurrent ? css.pillCurrent : css.pill}>S{season}</span>;
});
