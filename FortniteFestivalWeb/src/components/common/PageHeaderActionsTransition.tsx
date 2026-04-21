import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { FAST_FADE_MS, TRANSITION_MS } from '@festival/theme';
import { useSettings } from '../../contexts/SettingsContext';

type Phase = 'hidden' | 'visible' | 'pre-enter' | 'expanding' | 'entering' | 'fading' | 'collapsing';

interface PageHeaderActionsTransitionProps {
  visible: boolean;
  children: ReactNode;
  testId?: string;
}

export default function PageHeaderActionsTransition({ visible, children, testId }: PageHeaderActionsTransitionProps) {
  const { pendingMobileHeaderTransitionToken, consumeMobileHeaderTransitionToken } = useSettings();
  const initialEnterTokenRef = useRef<number | null>(visible ? pendingMobileHeaderTransitionToken : null);
  const pendingTransitionTokenRef = useRef<number | null>(pendingMobileHeaderTransitionToken);
  const [mounted, setMounted] = useState(visible);
  const [phase, setPhase] = useState<Phase>(visible
    ? (initialEnterTokenRef.current != null ? 'pre-enter' : 'visible')
    : 'hidden');
  const [measuredWidth, setMeasuredWidth] = useState(0);
  const outerRef = useRef<HTMLDivElement>(null);
  const lastChildrenRef = useRef(children);
  const initialRenderRef = useRef(true);
  const phaseRef = useRef<Phase>(phase);
  const timersRef = useRef<number[]>([]);

  pendingTransitionTokenRef.current = pendingMobileHeaderTransitionToken;

  if (visible && children != null) {
    lastChildrenRef.current = children;
  }

  const clearTimers = useCallback(() => {
    for (const timer of timersRef.current) {
      window.clearTimeout(timer);
    }
    timersRef.current = [];
  }, []);

  const measureWidth = useCallback(() => {
    const element = outerRef.current;
    if (!element) return;
    const nextWidth = element.scrollWidth;
    if (nextWidth > 0) {
      setMeasuredWidth(previous => previous === nextWidth ? previous : nextWidth);
    }
  }, []);

  useEffect(() => {
    phaseRef.current = phase;
  }, [phase]);

  const scheduleEnter = useCallback(() => {
    setMounted(true);
    setPhase('pre-enter');

    timersRef.current.push(window.setTimeout(() => {
      measureWidth();
      setPhase('expanding');
    }, 0));

    timersRef.current.push(window.setTimeout(() => {
      measureWidth();
      setPhase('entering');
    }, TRANSITION_MS));

    timersRef.current.push(window.setTimeout(() => {
      setPhase('visible');
    }, TRANSITION_MS + FAST_FADE_MS + 50));
  }, [measureWidth]);

  const scheduleExit = useCallback(() => {
    if (phaseRef.current === 'pre-enter') {
      setMounted(false);
      setPhase('hidden');
      return;
    }

    measureWidth();
    setPhase('fading');

    timersRef.current.push(window.setTimeout(() => {
      measureWidth();
      setPhase('collapsing');
    }, FAST_FADE_MS));

    timersRef.current.push(window.setTimeout(() => {
      setMounted(false);
      setPhase('hidden');
    }, FAST_FADE_MS + TRANSITION_MS));
  }, [measureWidth]);

  const consumePendingTransition = useCallback((token: number | null): boolean => {
    if (token == null) {
      return false;
    }

    consumeMobileHeaderTransitionToken(token);
    return true;
  }, [consumeMobileHeaderTransitionToken]);

  useLayoutEffect(() => {
    if (!mounted) return;
    measureWidth();
  }, [children, measureWidth, mounted, phase]);

  useEffect(() => {
    if (initialRenderRef.current) {
      initialRenderRef.current = false;
      if (initialEnterTokenRef.current != null) {
        consumePendingTransition(initialEnterTokenRef.current);
        scheduleEnter();
      }
      return clearTimers;
    }

    clearTimers();

    if (visible) {
      if (!consumePendingTransition(pendingTransitionTokenRef.current)) {
        measureWidth();
        setPhase('visible');
        setMounted(true);
        return clearTimers;
      }

      scheduleEnter();
      return clearTimers;
    }

    if (phaseRef.current === 'hidden') {
      setMounted(false);
      return clearTimers;
    }

    if (!consumePendingTransition(pendingTransitionTokenRef.current)) {
      setMounted(false);
      setPhase('hidden');
      return clearTimers;
    }

    scheduleExit();
    return clearTimers;
  }, [clearTimers, consumePendingTransition, measureWidth, scheduleEnter, scheduleExit, visible]);

  useEffect(() => () => {
    clearTimers();
  }, [clearTimers]);

  const outerStyle = useMemo(() => {
    const maxWidth = phase === 'hidden' || phase === 'pre-enter' || phase === 'collapsing'
      ? '0px'
      : measuredWidth > 0 ? `${measuredWidth}px` : undefined;

    return {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'flex-end',
      overflow: 'hidden',
      minWidth: 0,
      maxWidth,
      transition: phase === 'expanding' || phase === 'collapsing'
        ? `max-width ${TRANSITION_MS}ms ease`
        : undefined,
      willChange: phase === 'expanding' || phase === 'collapsing' ? 'max-width' : undefined,
    } as CSSProperties;
  }, [measuredWidth, phase]);

  const contentStyle = useMemo(() => {
    if (phase === 'visible') return undefined;

    if (phase === 'entering') {
      return {
        opacity: 0,
        willChange: 'opacity',
        animation: `fadeIn ${FAST_FADE_MS}ms ease-out forwards`,
      } as CSSProperties;
    }

    if (phase === 'fading') {
      return {
        opacity: 0,
        transition: `opacity ${FAST_FADE_MS}ms ease-out`,
        willChange: 'opacity',
        pointerEvents: 'none',
      } as CSSProperties;
    }

    return {
      opacity: 0,
      willChange: 'opacity',
      pointerEvents: 'none',
    } as CSSProperties;
  }, [phase]);

  if (!mounted) return null;

  return (
    <div ref={outerRef} data-testid={testId} style={outerStyle}>
      <div aria-hidden={phase !== 'visible' && phase !== 'entering'} style={contentStyle}>
        {lastChildrenRef.current}
      </div>
    </div>
  );
}