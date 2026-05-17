import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type MouseEvent as ReactMouseEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import { scheduleCompatibilityClickSuppression } from './pressCompatibilityClickSuppression';
import { clearPressPulse, startPressPulse } from './pressVisualFeedback';

const DEFAULT_MOVEMENT_THRESHOLD = 12;
const DEFAULT_CLICK_SUPPRESSION_MS = 700;
const DEFAULT_CARD_VERTICAL_MOVEMENT_THRESHOLD = 4;
const DEFAULT_CARD_HORIZONTAL_MOVEMENT_THRESHOLD = 10;
const DEFAULT_CARD_LONG_PRESS_MS = 500;
const DEFAULT_CARD_IGNORE_SELECTOR = 'a,button,[role="button"],input,select,textarea,[data-press-stop]';

type PressTrigger = 'click' | 'pointerup';
type CardPressTrigger = PressTrigger | 'keyboard';

type PressEvent<T extends Element> = ReactMouseEvent<T> | ReactPointerEvent<T>;
type CardPressEvent<T extends HTMLElement> = PressEvent<T> | ReactKeyboardEvent<T>;

interface UsePressActionOptions<T extends Element> {
  onPress: (event: PressEvent<T>, trigger: PressTrigger) => void;
  disabled?: boolean;
  movementThreshold?: number;
  clickSuppressionMs?: number;
  preventDefault?: boolean;
  stopPropagation?: boolean;
}

interface UseCardPressActionOptions<T extends HTMLElement> {
  onPress: (event: CardPressEvent<T>, trigger: CardPressTrigger) => void;
  disabled?: boolean;
  verticalMovementThreshold?: number;
  horizontalMovementThreshold?: number;
  longPressMs?: number;
  clickSuppressionMs?: number;
  ignoreDescendantSelector?: string;
}

export function usePressAction<T extends Element>({
  onPress,
  disabled = false,
  movementThreshold = DEFAULT_MOVEMENT_THRESHOLD,
  clickSuppressionMs = DEFAULT_CLICK_SUPPRESSION_MS,
  preventDefault = true,
  stopPropagation = false,
}: UsePressActionOptions<T>) {
  const pendingPointerRef = useRef<{ pointerId: number; clientX: number; clientY: number } | null>(null);
  const lastPointerPressRef = useRef<number | null>(null);

  const blockEvent = useCallback((event: PressEvent<T>) => {
    if (preventDefault) event.preventDefault();
    if (stopPropagation) event.stopPropagation();
  }, [preventDefault, stopPropagation]);

  const commitPress = useCallback((event: PressEvent<T>, trigger: PressTrigger) => {
    blockEvent(event);
    onPress(event, trigger);
  }, [blockEvent, onPress]);

  const onPointerDown = useCallback((event: ReactPointerEvent<T>) => {
    if (disabled || event.button !== 0 || event.pointerType === 'mouse') return;
    pendingPointerRef.current = { pointerId: event.pointerId, clientX: event.clientX, clientY: event.clientY };
  }, [disabled]);

  const onPointerUp = useCallback((event: ReactPointerEvent<T>) => {
    const pendingPointer = pendingPointerRef.current;
    pendingPointerRef.current = null;
    if (disabled || !pendingPointer || pendingPointer.pointerId !== event.pointerId) return;

    const moved = Math.hypot(event.clientX - pendingPointer.clientX, event.clientY - pendingPointer.clientY);
    if (moved > movementThreshold) return;

    lastPointerPressRef.current = event.timeStamp;
    scheduleCompatibilityClickSuppression(event, clickSuppressionMs);
    commitPress(event, 'pointerup');
  }, [clickSuppressionMs, commitPress, disabled, movementThreshold]);

  const onPointerCancel = useCallback(() => {
    pendingPointerRef.current = null;
  }, []);

  const onClick = useCallback((event: ReactMouseEvent<T>) => {
    if (disabled) {
      blockEvent(event);
      return;
    }

    const lastPointerPress = lastPointerPressRef.current;
    if (lastPointerPress !== null && event.timeStamp - lastPointerPress < clickSuppressionMs) {
      blockEvent(event);
      return;
    }

    commitPress(event, 'click');
  }, [blockEvent, clickSuppressionMs, commitPress, disabled]);

  return { onPointerDown, onPointerUp, onPointerCancel, onClick };
}

export function useCardPressAction<T extends HTMLElement>({
  onPress,
  disabled = false,
  verticalMovementThreshold = DEFAULT_CARD_VERTICAL_MOVEMENT_THRESHOLD,
  horizontalMovementThreshold = DEFAULT_CARD_HORIZONTAL_MOVEMENT_THRESHOLD,
  longPressMs = DEFAULT_CARD_LONG_PRESS_MS,
  clickSuppressionMs = DEFAULT_CLICK_SUPPRESSION_MS,
  ignoreDescendantSelector = DEFAULT_CARD_IGNORE_SELECTOR,
}: UseCardPressActionOptions<T>) {
  const pendingPointerRef = useRef<{ pointerId: number; clientX: number; clientY: number; timeStamp: number } | null>(null);
  const lastPointerPressRef = useRef<number | null>(null);
  const pressPulseTargetRef = useRef<HTMLElement | null>(null);
  const [isPressed, setIsPressed] = useState(false);

  const cancelPendingPress = useCallback(() => {
    pendingPointerRef.current = null;
    setIsPressed(false);
    clearPressPulse(pressPulseTargetRef.current);
    pressPulseTargetRef.current = null;
  }, []);

  useEffect(() => {
    const handleVisibilityChange = () => {
      if (document.visibilityState === 'hidden') cancelPendingPress();
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    window.addEventListener('blur', cancelPendingPress);
    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      window.removeEventListener('blur', cancelPendingPress);
    };
  }, [cancelPendingPress]);

  const isNestedInteractive = useCallback((event: { currentTarget: T; target: EventTarget | null }) => {
    if (!event.target || !(event.target instanceof Element)) return false;
    const interactive = event.target.closest(ignoreDescendantSelector);
    return !!interactive && interactive !== event.currentTarget && event.currentTarget.contains(interactive);
  }, [ignoreDescendantSelector]);

  const movedBeyondThreshold = useCallback((event: ReactPointerEvent<T>, pendingPointer: { clientX: number; clientY: number }) => {
    return Math.abs(event.clientY - pendingPointer.clientY) > verticalMovementThreshold
      || Math.abs(event.clientX - pendingPointer.clientX) > horizontalMovementThreshold;
  }, [horizontalMovementThreshold, verticalMovementThreshold]);

  const onPointerDown = useCallback((event: ReactPointerEvent<T>) => {
    if (disabled || event.button !== 0 || event.pointerType === 'mouse' || event.isPrimary === false || isNestedInteractive(event)) return;
    pendingPointerRef.current = {
      pointerId: event.pointerId,
      clientX: event.clientX,
      clientY: event.clientY,
      timeStamp: event.timeStamp,
    };
    pressPulseTargetRef.current = startPressPulse(event);
    setIsPressed(true);
  }, [disabled, isNestedInteractive]);

  const onPointerMove = useCallback((event: ReactPointerEvent<T>) => {
    const pendingPointer = pendingPointerRef.current;
    if (!pendingPointer || pendingPointer.pointerId !== event.pointerId) return;
    if (movedBeyondThreshold(event, pendingPointer)) cancelPendingPress();
  }, [cancelPendingPress, movedBeyondThreshold]);

  const onPointerUp = useCallback((event: ReactPointerEvent<T>) => {
    const pendingPointer = pendingPointerRef.current;
    const pressPulseTarget = pressPulseTargetRef.current;
    pendingPointerRef.current = null;
    pressPulseTargetRef.current = null;
    setIsPressed(false);
    if (disabled || !pendingPointer || pendingPointer.pointerId !== event.pointerId || isNestedInteractive(event)) {
      clearPressPulse(pressPulseTarget);
      return;
    }
    if (movedBeyondThreshold(event, pendingPointer)) {
      clearPressPulse(pressPulseTarget);
      return;
    }
    if (event.timeStamp - pendingPointer.timeStamp > longPressMs) {
      clearPressPulse(pressPulseTarget);
      return;
    }

    lastPointerPressRef.current = event.timeStamp;
  scheduleCompatibilityClickSuppression(event, clickSuppressionMs);
    onPress(event, 'pointerup');
  }, [clickSuppressionMs, disabled, isNestedInteractive, longPressMs, movedBeyondThreshold, onPress]);

  const onClick = useCallback((event: ReactMouseEvent<T>) => {
    if (disabled) {
      event.preventDefault();
      return;
    }
    if (isNestedInteractive(event)) return;

    const lastPointerPress = lastPointerPressRef.current;
    if (lastPointerPress !== null && event.timeStamp - lastPointerPress < clickSuppressionMs) {
      event.preventDefault();
      return;
    }

    onPress(event, 'click');
  }, [clickSuppressionMs, disabled, isNestedInteractive, onPress]);

  const onKeyDown = useCallback((event: ReactKeyboardEvent<T>) => {
    if (disabled || event.repeat || isNestedInteractive(event)) return;
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    onPress(event, 'keyboard');
  }, [disabled, isNestedInteractive, onPress]);

  return {
    isPressed,
    pressHandlers: {
      onPointerDown,
      onPointerMove,
      onPointerUp,
      onPointerCancel: cancelPendingPress,
      onPointerLeave: cancelPendingPress,
      onBlur: cancelPendingPress,
      onClick,
      onKeyDown,
    },
  };
}
