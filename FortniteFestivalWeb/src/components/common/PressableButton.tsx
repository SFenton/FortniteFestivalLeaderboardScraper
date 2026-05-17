import { forwardRef, type ButtonHTMLAttributes } from 'react';
import { usePressAction } from '../../hooks/ui/usePressAction';

export interface PressableButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'onClick' | 'onPointerDown' | 'onPointerUp' | 'onPointerCancel'> {
  onPress: () => void;
  clickSuppressionMs?: number;
  movementThreshold?: number;
  preventDefault?: boolean;
  stopPropagation?: boolean;
}

export const PressableButton = forwardRef<HTMLButtonElement, PressableButtonProps>(function PressableButton({
  onPress,
  disabled,
  type = 'button',
  clickSuppressionMs,
  movementThreshold,
  preventDefault,
  stopPropagation,
  ...buttonProps
}, ref) {
  const pressHandlers = usePressAction<HTMLButtonElement>({
    onPress,
    disabled,
    clickSuppressionMs,
    movementThreshold,
    preventDefault,
    stopPropagation,
  });

  return (
    <button
      ref={ref}
      type={type}
      disabled={disabled}
      {...buttonProps}
      {...pressHandlers}
    />
  );
});

export default PressableButton;