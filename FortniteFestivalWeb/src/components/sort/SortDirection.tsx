/**
 * Reusable sort direction toggle (ascending/descending) with animated arrow buttons.
 * Shared between SortModal and PlayerScoreSortModal.
 */
import { useTranslation } from 'react-i18next';
import { IoArrowUp, IoArrowDown } from 'react-icons/io5';
import { Colors, Size } from '@festival/theme';
import css from './SortDirection.module.css';

export interface SortDirectionProps {
  ascending: boolean;
  onChange: (ascending: boolean) => void;
  title?: string;
  ascLabel?: string;
  descLabel?: string;
}

export default function SortDirection({ ascending, onChange, title, ascLabel, descLabel }: SortDirectionProps) {
  const { t } = useTranslation();
  return (
    <div className={css.inner}>
      <div className={css.textCol}>
        <div className={css.title}>{title ?? t('sort.direction')}</div>
        <div className={css.hint}>
          {ascending ? (ascLabel ?? t('sort.ascending')) : (descLabel ?? t('sort.descending'))}
        </div>
      </div>
      <div className={css.icons}>
        <button
          className={css.iconBtn}
          onClick={() => onChange(true)}
          aria-label={t('aria.ascending')}
        >
          <div className={ascending ? css.iconCircleActive : css.iconCircle} />
          <IoArrowUp size={Size.iconTab} className={css.arrowIcon} style={{ color: ascending ? Colors.textPrimary : Colors.textMuted }} />
        </button>
        <button
          className={css.iconBtn}
          onClick={() => onChange(false)}
          aria-label={t('aria.descending')}
        >
          <div className={!ascending ? css.iconCircleActive : css.iconCircle} />
          <IoArrowDown size={Size.iconTab} className={css.arrowIcon} style={{ color: !ascending ? Colors.textPrimary : Colors.textMuted }} />
        </button>
      </div>
    </div>
  );
}
