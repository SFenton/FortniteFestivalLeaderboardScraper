/**
 * Reusable sort direction toggle (ascending/descending) with animated arrow buttons.
 * Shared between SortModal and PlayerScoreSortModal.
 */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoArrowUp, IoArrowDown } from 'react-icons/io5';
import {
  Colors, Font, Weight, Gap, IconSize, ZIndex,
  Position, Cursor, Overflow, CssValue, CssProp,
  flexRow, flexColumn, flexCenter, absoluteFill,
  transition, scale, FAST_FADE_MS, QUICK_FADE_MS,
} from '@festival/theme';

export interface SortDirectionProps {
  ascending: boolean;
  onChange: (ascending: boolean) => void;
  title?: string;
  ascLabel?: string;
  descLabel?: string;
}

export default function SortDirection({ ascending, onChange, title, ascLabel, descLabel }: SortDirectionProps) {
  const { t } = useTranslation();
  const s = useStyles(ascending);
  return (
    <div style={s.inner}>
      <div style={s.textCol}>
        <div style={s.title}>{title ?? t('sort.direction')}</div>
        <div style={s.hint}>
          {ascending ? (ascLabel ?? t('sort.ascending')) : (descLabel ?? t('sort.descending'))}
        </div>
      </div>
      <div style={s.icons}>
        <button
          style={s.iconBtn}
          onClick={() => onChange(true)}
          aria-label={t('aria.ascending')}
        >
          <div style={s.ascCircle} />
          <IoArrowUp size={IconSize.tab} style={s.ascArrow} />
        </button>
        <button
          style={s.iconBtn}
          onClick={() => onChange(false)}
          aria-label={t('aria.descending')}
        >
          <div style={s.descCircle} />
          <IoArrowDown size={IconSize.tab} style={s.descArrow} />
        </button>
      </div>
    </div>
  );
}

function useStyles(ascending: boolean) {
  return useMemo(() => {
    const circleBase: CSSProperties = {
      ...absoluteFill,
      borderRadius: CssValue.circle,
      backgroundColor: Colors.accentPurple,
      transition: transition(CssProp.transform, FAST_FADE_MS),
    };
    const arrowBase: CSSProperties = {
      position: Position.relative,
      zIndex: ZIndex.base,
      transition: transition(CssProp.color, QUICK_FADE_MS),
    };
    return {
      inner: {
        ...flexRow,
        gap: Gap.xl,
        paddingBottom: Gap.md,
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
        marginRight: -Gap.xl,
      } as CSSProperties,
      iconBtn: {
        width: IconSize.lg,
        height: IconSize.lg,
        borderRadius: CssValue.circle,
        border: CssValue.none,
        backgroundColor: CssValue.transparent,
        ...flexCenter,
        cursor: Cursor.pointer,
        position: Position.relative,
        overflow: Overflow.hidden,
      } as CSSProperties,
      ascCircle: { ...circleBase, transform: ascending ? scale(1) : scale(0) } as CSSProperties,
      descCircle: { ...circleBase, transform: !ascending ? scale(1) : scale(0) } as CSSProperties,
      ascArrow: { ...arrowBase, color: ascending ? Colors.textPrimary : Colors.textMuted } as CSSProperties,
      descArrow: { ...arrowBase, color: !ascending ? Colors.textPrimary : Colors.textMuted } as CSSProperties,
    };
  }, [ascending]);
}
