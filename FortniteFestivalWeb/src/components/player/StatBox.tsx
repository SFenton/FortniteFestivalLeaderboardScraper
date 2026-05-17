import { memo, useMemo, type CSSProperties } from 'react';
import {
  Colors, Font, Weight, Gap, Overflow, Cursor, Position,
  TextAlign, TextTransform, FontVariant, WordBreak,
  flexColumn, centerVertical, padding, scale, transition, transitions,
} from '@festival/theme';
import { useCardPressAction } from '../../hooks/ui/usePressAction';

interface StatBoxProps {
  label: string;
  value: React.ReactNode;
  color?: string;
  onClick?: () => void;
}

const StatBox = memo(function StatBox({ label, value, color, onClick }: StatBoxProps) {
  const cardPress = useCardPressAction<HTMLDivElement>({
    onPress: () => { onClick?.(); },
    disabled: !onClick,
  });
  const s = useStyles(color, !!onClick);
  const inner = (
    <div style={s.box}>
      <span style={s.value}>{value}</span>
      <span style={s.label}>{label}</span>
    </div>
  );
  if (onClick) {
    return (
    <div
      style={{ ...s.clickable, ...(cardPress.isPressed ? s.clickablePressed : undefined) }}
      role="button"
      tabIndex={0}
      data-pressed={cardPress.isPressed ? 'true' : undefined}
      {...cardPress.pressHandlers}
    >
      {inner}
      <svg style={s.chevron} width="8" height="14" viewBox="0 0 8 14" fill="none">
        <path d="M1.5 1.5L6.5 7L1.5 12.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
      </svg>
    </div>
  );
  }
  return inner;
});

export default StatBox;

function useStyles(color?: string, clickable?: boolean) {
  return useMemo(() => ({
    box: {
      ...flexColumn,
      alignItems: 'center',
      padding: padding(Gap.xl, Gap.md),
      minWidth: 0,
      overflow: Overflow.hidden,
    } as CSSProperties,
    value: {
      fontSize: Font.xl,
      fontWeight: Weight.bold,
      color: color ?? Colors.accentBlueBright,
      marginBottom: Gap.xs,
      wordBreak: WordBreak.breakWord,
      textAlign: TextAlign.center,
      fontVariantNumeric: FontVariant.tabularNums,
    } as CSSProperties,
    label: {
      fontSize: Font.xs,
      color: Colors.textTertiary,
      textTransform: TextTransform.uppercase,
      letterSpacing: Font.letterSpacingWide,
    } as CSSProperties,
    clickable: {
      cursor: Cursor.pointer,
      position: Position.relative,
      touchAction: 'manipulation',
      transition: transitions(transition('transform', 80), transition('background-color', 80)),
      transform: scale(1),
    } as CSSProperties,
    clickablePressed: {
      transform: scale(0.985),
      backgroundColor: 'rgba(255, 255, 255, 0.04)',
    } as CSSProperties,
    chevron: {
      position: Position.absolute,
      right: Gap.xl,
      ...centerVertical,
      color: Colors.textPrimary,
    } as CSSProperties,
  }), [color, clickable]);
}
