/**
 * A single frosted-glass pagination button.
 * Consumers own the layout container; this is just the button.
 */
import { memo, useMemo, type ReactNode, type CSSProperties } from 'react';
import { Colors, Font, Weight, Radius, Gap, Opacity, Cursor, CssProp, frostedCard, padding, transition, QUICK_FADE_MS } from '@festival/theme';

export interface PaginationButtonProps {
  children: ReactNode;
  disabled?: boolean;
  onClick: () => void;
}

export const PaginationButton = memo(function PaginationButton({
  children,
  disabled,
  onClick,
}: PaginationButtonProps) {
  const s = useStyles(disabled);
  return (
    <button
      style={s.button}
      disabled={disabled}
      onClick={onClick}
    >
      {children}
    </button>
  );
});

function useStyles(disabled?: boolean) {
  return useMemo(() => ({
    button: {
      ...frostedCard,
      borderRadius: Radius.sm,
      padding: padding(Gap.md, Gap.xl),
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      color: Colors.textPrimary,
      cursor: disabled ? Cursor.default : Cursor.pointer,
      opacity: disabled ? Opacity.faded : undefined,
      transition: transition(CssProp.backgroundColor, QUICK_FADE_MS),
    } as CSSProperties,
  }), [disabled]);
}
