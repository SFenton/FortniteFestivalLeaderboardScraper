import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { FAST_FADE_MS, FADE_DURATION, TRANSITION_MS } from '@festival/theme';
import { useSettings } from '../../contexts/SettingsContext';

type Phase = 'hidden' | 'visible' | 'pre-enter' | 'expanding' | 'entering' | 'fading' | 'collapsing';

interface PageHeaderTransitionProps {
  visible: boolean;
  children: ReactNode;
  testId?: string;
}

export default function PageHeaderTransition({ visible, children, testId }: PageHeaderTransitionProps) {
  const { pendingMobileHeaderTransitionToken, consumeMobileHeaderTransitionToken } = useSettings();
  const initialEnterTokenRef = useRef<number | null>(visible ? pendingMobileHeaderTransitionToken : null);
  const pendingTransitionTokenRef = useRef<number | null>(pendingMobileHeaderTransitionToken);
  const [mounted, setMounted] = useState(visible);
  const [phase, setPhase] = useState<Phase>(visible
    ? (initialEnterTokenRef.current != null ? 'pre-enter' : 'visible')
    : 'hidden');
  const [measuredHeight, setMeasuredHeight] = useState(0);
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

  const measureHeight = useCallback(() => {
    const element = outerRef.current;
    if (!element) return;
    const nextHeight = element.scrollHeight;
    if (nextHeight > 0) {
      setMeasuredHeight(previous => previous === nextHeight ? previous : nextHeight);
    }
  }, []);

  useEffect(() => {
    phaseRef.current = phase;
  }, [phase]);

  const scheduleEnter = useCallback(() => {
    setMounted(true);
    setPhase('pre-enter');

    timersRef.current.push(window.setTimeout(() => {
      measureHeight();
      setPhase('expanding');
    }, 0));

    timersRef.current.push(window.setTimeout(() => {
      measureHeight();
      setPhase('entering');
    }, TRANSITION_MS));

    timersRef.current.push(window.setTimeout(() => {
      setPhase('visible');
    }, TRANSITION_MS + FADE_DURATION + 50));
  }, [measureHeight]);

  const scheduleExit = useCallback(() => {
    if (phaseRef.current === 'pre-enter') {
      setMounted(false);
      setPhase('hidden');
      return;
    }

    measureHeight();
    setPhase('fading');

    timersRef.current.push(window.setTimeout(() => {
      measureHeight();
      setPhase('collapsing');
    }, FAST_FADE_MS));

    timersRef.current.push(window.setTimeout(() => {
      setMounted(false);
      setPhase('hidden');
    }, FAST_FADE_MS + TRANSITION_MS));
  }, [measureHeight]);

  const consumePendingTransition = useCallback((token: number | null): boolean => {
    if (token == null) {
      return false;
    }

    consumeMobileHeaderTransitionToken(token);
    return true;
  }, [consumeMobileHeaderTransitionToken]);

  useLayoutEffect(() => {
    if (!mounted) return;
    measureHeight();
  }, [mounted, phase, children, measureHeight]);

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
        measureHeight();
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
  }, [clearTimers, consumePendingTransition, measureHeight, scheduleEnter, scheduleExit, visible]);

  useEffect(() => () => {
    clearTimers();
  }, [clearTimers]);

  const outerStyle = useMemo(() => {
    const maxHeight = phase === 'hidden' || phase === 'pre-enter' || phase === 'collapsing'
      ? '0px'
      : measuredHeight > 0 ? `${measuredHeight}px` : undefined;

    return {
      overflow: phase === 'visible' ? 'visible' : 'hidden',
      maxHeight,
      transition: phase === 'expanding' || phase === 'collapsing'
        ? `max-height ${TRANSITION_MS}ms ease`
        : undefined,
      willChange: phase === 'expanding' || phase === 'collapsing' ? 'max-height' : undefined,
    } as CSSProperties;
  }, [measuredHeight, phase]);

  const contentStyle = useMemo(() => {
    if (phase === 'visible') return undefined;

    if (phase === 'entering') {
      return {
        opacity: 0,
        willChange: 'opacity, transform',
        animation: `fadeInUp ${FADE_DURATION}ms ease-out forwards`,
      } as CSSProperties;
    }

    if (phase === 'fading') {
      return {
        opacity: 0,
        transform: 'translateY(12px)',
        transition: `opacity ${FAST_FADE_MS}ms ease-out, transform ${FAST_FADE_MS}ms ease-out`,
        willChange: 'opacity, transform',
        pointerEvents: 'none',
      } as CSSProperties;
    }

    return {
      opacity: 0,
      transform: 'translateY(12px)',
      willChange: 'opacity, transform',
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