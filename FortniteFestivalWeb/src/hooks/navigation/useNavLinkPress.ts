import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type MouseEvent as ReactMouseEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import { useNavigate, type NavigateOptions, type To } from 'react-router-dom';

const DEFAULT_MOVEMENT_THRESHOLD = 12;
const DEFAULT_LONG_PRESS_MS = 500;
const DEFAULT_CLICK_SUPPRESSION_MS = 700;

type PendingPointer = {
  pointerId: number;
  clientX: number;
  clientY: number;
  timeStamp: number;
};

type NavLinkPressOptions = NavigateOptions & {
  to: To;
  disabled?: boolean;
  target?: string;
  download?: boolean | string;
  onNavigate?: () => void;
  movementThreshold?: number;
  longPressMs?: number;
  clickSuppressionMs?: number;
};

function hasModifiedIntent(event: Pick<MouseEvent, 'altKey' | 'ctrlKey' | 'metaKey' | 'shiftKey'>) {
  return event.altKey || event.ctrlKey || event.metaKey || event.shiftKey;
}

function shouldUseNativeNavigation(target?: string, download?: boolean | string) {
  return !!download || (!!target && target !== '_self');
}

export function useNavLinkPress<T extends HTMLAnchorElement>({
  to,
  disabled = false,
  target,
  download,
  movementThreshold = DEFAULT_MOVEMENT_THRESHOLD,
  longPressMs = DEFAULT_LONG_PRESS_MS,
  clickSuppressionMs = DEFAULT_CLICK_SUPPRESSION_MS,
  replace,
  state,
  preventScrollReset,
  relative,
  viewTransition,
  onNavigate,
}: NavLinkPressOptions) {
  const navigate = useNavigate();
  const pendingPointerRef = useRef<PendingPointer | null>(null);
  const lastPointerNavigationRef = useRef<number | null>(null);
  const [isPressed, setIsPressed] = useState(false);

  const cancelPendingPress = useCallback(() => {
    pendingPointerRef.current = null;
    setIsPressed(false);
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

  const canInterceptPointer = useCallback((event: ReactPointerEvent<T>) => {
    return !disabled
      && event.button === 0
      && event.pointerType !== 'mouse'
      && event.isPrimary !== false
      && !hasModifiedIntent(event)
      && !shouldUseNativeNavigation(target, download);
  }, [disabled, download, target]);

  const movedBeyondThreshold = useCallback((event: ReactPointerEvent<T>, pendingPointer: PendingPointer) => {
    return Math.hypot(event.clientX - pendingPointer.clientX, event.clientY - pendingPointer.clientY) > movementThreshold;
  }, [movementThreshold]);

  const navigateNow = useCallback(() => {
    onNavigate?.();
    navigate(to, { replace, state, preventScrollReset, relative, viewTransition });
  }, [navigate, onNavigate, preventScrollReset, relative, replace, state, to, viewTransition]);

  const onPointerDown = useCallback((event: ReactPointerEvent<T>) => {
    if (!canInterceptPointer(event)) return;
    pendingPointerRef.current = {
      pointerId: event.pointerId,
      clientX: event.clientX,
      clientY: event.clientY,
      timeStamp: event.timeStamp,
    };
    setIsPressed(true);
  }, [canInterceptPointer]);

  const onPointerMove = useCallback((event: ReactPointerEvent<T>) => {
    const pendingPointer = pendingPointerRef.current;
    if (!pendingPointer || pendingPointer.pointerId !== event.pointerId) return;
    if (movedBeyondThreshold(event, pendingPointer)) cancelPendingPress();
  }, [cancelPendingPress, movedBeyondThreshold]);

  const onPointerUp = useCallback((event: ReactPointerEvent<T>) => {
    const pendingPointer = pendingPointerRef.current;
    pendingPointerRef.current = null;
    setIsPressed(false);
    if (!pendingPointer || pendingPointer.pointerId !== event.pointerId || !canInterceptPointer(event)) return;
    if (movedBeyondThreshold(event, pendingPointer)) return;
    if (event.timeStamp - pendingPointer.timeStamp > longPressMs) return;

    event.preventDefault();
    lastPointerNavigationRef.current = event.timeStamp;
    navigateNow();
  }, [canInterceptPointer, longPressMs, movedBeyondThreshold, navigateNow]);

  const onClick = useCallback((event: ReactMouseEvent<T>) => {
    const lastPointerNavigation = lastPointerNavigationRef.current;
    if (lastPointerNavigation !== null && event.timeStamp - lastPointerNavigation < clickSuppressionMs) {
      event.preventDefault();
      return;
    }
    if (disabled || event.defaultPrevented || hasModifiedIntent(event) || shouldUseNativeNavigation(target, download)) return;
    onNavigate?.();
  }, [clickSuppressionMs, disabled, download, onNavigate, target]);

  return {
    isPressed,
    linkPressHandlers: {
      onPointerDown,
      onPointerMove,
      onPointerUp,
      onPointerCancel: cancelPendingPress,
      onPointerLeave: cancelPendingPress,
      onBlur: cancelPendingPress,
      onClick,
    },
  };
}