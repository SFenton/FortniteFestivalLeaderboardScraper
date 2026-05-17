/* eslint-disable react/forbid-dom-props -- press state is expressed through inline theme styles */
import { type AnimationEventHandler, type CSSProperties, type ReactNode } from 'react';
import { useCardPressAction } from '../../hooks/ui/usePressAction';

interface CardPressableProps {
  children: ReactNode;
  onPress: () => void;
  className?: string;
  style?: CSSProperties;
  pressedStyle?: CSSProperties;
  testId?: string;
  ariaLabel?: string;
  onAnimationEnd?: AnimationEventHandler<HTMLDivElement>;
}

export default function CardPressable({
  children,
  onPress,
  className,
  style,
  pressedStyle,
  testId,
  ariaLabel,
  onAnimationEnd,
}: CardPressableProps) {
  const cardPress = useCardPressAction<HTMLDivElement>({ onPress });

  return (
    <div
      className={className}
      data-card-pressable=""
      data-testid={testId}
      role="button"
      tabIndex={0}
      aria-label={ariaLabel}
      data-pressed={cardPress.isPressed ? 'true' : undefined}
      style={{ touchAction: 'manipulation', ...style, ...(cardPress.isPressed ? pressedStyle : undefined) }}
      onAnimationEnd={onAnimationEnd}
      {...cardPress.pressHandlers}
    >
      {children}
    </div>
  );
}