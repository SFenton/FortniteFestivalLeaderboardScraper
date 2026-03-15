import { memo } from 'react';
import css from './SeasonPill.module.css';

export default memo(function SeasonPill({ season }: { season: number }) {
  return <span className={css.pill}>S{season}</span>;
});
