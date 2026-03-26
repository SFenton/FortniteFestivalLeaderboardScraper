/**
 * Ascending / Descending toggle used in both the Sort first-run slide and SortModal.
 * Purple circle animation on the active direction, matching the production style.
 */
import { memo, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoArrowUp, IoArrowDown } from 'react-icons/io5';
import {
  Colors, Font, Weight, Gap, IconSize, ZIndex, LineHeight,
  Position, Cursor, Overflow, CssValue, CssProp,
  flexRow, flexColumn, flexCenter, absoluteFill,
  transition, scale, FAST_FADE_MS,
} from '@festival/theme';

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
  const s = useStyles(ascending);
  const resolvedTitle = title ?? t('sort.direction');
  const desc = hint ?? (ascending ? t('sort.ascendingHintSongs') : t('sort.descendingHintSongs'));
  return (
    <div style={s.inner}>
      <div style={s.textCol}>
        <div style={s.title}>{resolvedTitle}</div>
        <div style={s.hint}>{desc}</div>
      </div>
      <div style={s.icons}>
        <button style={s.iconBtn} onClick={() => onChange(true)} aria-label={t('sort.ascending')}>
          <div style={s.ascCircle} />
          <IoArrowUp size={IconSize.default} style={s.ascIcon} />
        </button>
        <button style={s.iconBtn} onClick={() => onChange(false)} aria-label={t('sort.descending')}>
          <div style={s.descCircle} />
          <IoArrowDown size={IconSize.default} style={s.descIcon} />
        </button>
      </div>
    </div>
  );
});

function useStyles(ascending: boolean) {
  return useMemo(() => {
    const circleBase: CSSProperties = {
      ...absoluteFill,
      borderRadius: CssValue.circle,
      backgroundColor: Colors.accentPurple,
      transition: transition(CssProp.transform, FAST_FADE_MS),
    };
    const iconBase: CSSProperties = {
      position: Position.relative,
      zIndex: ZIndex.base,
      transition: transition(CssProp.color, FAST_FADE_MS),
    };
    return {
      inner: {
        ...flexRow,
        gap: Gap.xl,
      } as CSSProperties,
      textCol: {
        ...flexColumn,
        flex: 1,
        gap: Gap.xs,
      } as CSSProperties,
      title: {
        fontSize: Font.lg,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
      } as CSSProperties,
      hint: {
        fontSize: Font.sm,
        color: Colors.textSecondary,
      } as CSSProperties,
      icons: {
        ...flexRow,
        gap: Gap.md,
        flexShrink: 0,
      } as CSSProperties,
      iconBtn: {
        width: IconSize.xl,
        height: IconSize.xl,
        borderRadius: CssValue.circle,
        border: CssValue.none,
        background: CssValue.transparent,
        cursor: Cursor.pointer,
        position: Position.relative,
        ...flexCenter,
        overflow: Overflow.hidden,
        padding: Gap.none,
        lineHeight: LineHeight.none,
      } as CSSProperties,
      ascCircle: { ...circleBase, transform: ascending ? scale(1) : scale(0) } as CSSProperties,
      descCircle: { ...circleBase, transform: !ascending ? scale(1) : scale(0) } as CSSProperties,
      ascIcon: { ...iconBase, color: ascending ? Colors.textPrimary : Colors.textMuted } as CSSProperties,
      descIcon: { ...iconBase, color: !ascending ? Colors.textPrimary : Colors.textMuted } as CSSProperties,
    };
  }, [ascending]);
}
