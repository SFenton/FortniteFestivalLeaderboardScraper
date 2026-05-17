import { memo, useCallback, type CSSProperties, type KeyboardEvent, type SVGProps } from 'react';
import { Cursor } from '@festival/theme';
import { usePressAction } from '../../hooks/ui/usePressAction';

type PressableChartPathProps = Omit<SVGProps<SVGPathElement>, 'onClick' | 'onPointerCancel' | 'onPointerDown' | 'onPointerUp' | 'onKeyDown'> & {
  ariaLabel: string;
  disabled?: boolean;
  onPress: () => void;
};

function PressableChartPathInner({ ariaLabel, disabled = false, onPress, style, ...pathProps }: PressableChartPathProps) {
  const pressHandlers = usePressAction<SVGPathElement>({
    onPress,
    disabled,
    preventDefault: true,
  });

  const handleKeyDown = useCallback((event: KeyboardEvent<SVGPathElement>) => {
    if (disabled || event.repeat || (event.key !== 'Enter' && event.key !== ' ')) return;
    event.preventDefault();
    onPress();
  }, [disabled, onPress]);

  const mergedStyle: CSSProperties = {
    cursor: disabled ? Cursor.default : Cursor.pointer,
    touchAction: 'manipulation',
    ...(style as CSSProperties | undefined),
  };

  return (
    <path
      {...pathProps}
      {...pressHandlers}
      aria-label={ariaLabel}
      focusable={disabled ? 'false' : 'true'}
      role="button"
      tabIndex={disabled ? -1 : 0}
      onKeyDown={handleKeyDown}
      style={mergedStyle}
    />
  );
}

export const PressableChartPath = memo(PressableChartPathInner);