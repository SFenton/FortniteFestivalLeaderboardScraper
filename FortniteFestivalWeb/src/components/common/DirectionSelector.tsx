/**
 * Ascending / Descending toggle used in both the Sort first-run slide and SortModal.
 * Purple circle animation on the active direction, matching the production style.
 */
import { memo } from 'react';
import { useTranslation } from 'react-i18next';
import { IoArrowUp, IoArrowDown } from 'react-icons/io5';
import { Size } from '@festival/theme';
import css from './DirectionSelector.module.css';

export interface DirectionSelectorProps {
  ascending: boolean;
  onChange: (ascending: boolean) => void;
  title?: string;
  hint?: string;
}

export const DirectionSelector = memo(function DirectionSelector({
  ascending, onChange,
  title,
  hint,
}: DirectionSelectorProps) {
  const { t } = useTranslation();
  const resolvedTitle = title ?? t('sort.direction');
  const desc = hint ?? (ascending ? t('sort.ascendingHintSongs') : t('sort.descendingHintSongs'));
  return (
    <div className={css.inner}>
      <div className={css.textCol}>
        <div className={css.title}>{resolvedTitle}</div>
        <div className={css.hint}>{desc}</div>
      </div>
      <div className={css.icons}>
        <button className={css.iconBtn} onClick={() => onChange(true)} aria-label={t('sort.ascending')}>
          <div className={ascending ? css.iconCircleActive : css.iconCircle} />
          <IoArrowUp size={Size.iconDefault} className={ascending ? css.iconActive : css.icon} />
        </button>
        <button className={css.iconBtn} onClick={() => onChange(false)} aria-label={t('sort.descending')}>
          <div className={!ascending ? css.iconCircleActive : css.iconCircle} />
          <IoArrowDown size={Size.iconDefault} className={!ascending ? css.iconActive : css.icon} />
        </button>
      </div>
    </div>
  );
});
