import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { CssProp, Display, Overflow, FAST_FADE_MS, transition, transitions, translateY } from '@festival/theme';

type CollapsePresenceProps = {
  visible: boolean;
  children: ReactNode;
  durationMs?: number;
  exitOffsetY?: number;
  testId?: string;
};

export default function CollapsePresence({
  visible,
  children,
  durationMs = FAST_FADE_MS,
  exitOffsetY = -8,
  testId,
}: CollapsePresenceProps) {
  const [mounted, setMounted] = useState(visible);
  const [expanded, setExpanded] = useState(visible);
  const lastChildrenRef = useRef(children);
  const timerRef = useRef<number | null>(null);

  if (visible && children != null) {
    lastChildrenRef.current = children;
  }

  useEffect(() => () => {
    if (timerRef.current != null) {
      window.clearTimeout(timerRef.current);
    }
  }, []);

  useEffect(() => {
    if (timerRef.current != null) {
      window.clearTimeout(timerRef.current);
      timerRef.current = null;
    }

    if (visible) {
      setMounted(true);
      setExpanded(true);
      return;
    }

    if (!mounted) {
      return;
    }

    setExpanded(false);
    timerRef.current = window.setTimeout(() => {
      setMounted(false);
      timerRef.current = null;
    }, durationMs);
  }, [durationMs, mounted, visible]);

  const outerStyle = useMemo(() => ({
    display: Display.grid,
    gridTemplateRows: expanded ? '1fr' : '0fr',
    transition: transition(CssProp.gridTemplateRows, durationMs),
  }) as CSSProperties, [durationMs, expanded]);

  const innerStyle = useMemo(() => ({
    overflow: Overflow.hidden,
    minHeight: 0,
  }) as CSSProperties, []);

  const contentStyle = useMemo(() => ({
    opacity: expanded ? 1 : 0,
    transform: expanded ? 'translateY(0)' : translateY(exitOffsetY),
    transition: transitions(
      transition(CssProp.opacity, durationMs),
      transition(CssProp.transform, durationMs),
    ),
    pointerEvents: expanded ? undefined : 'none',
  }) as CSSProperties, [durationMs, expanded, exitOffsetY]);

  if (!mounted) {
    return null;
  }

  return (
    <div data-testid={testId} style={outerStyle}>
      <div style={innerStyle}>
        <div aria-hidden={!expanded} style={contentStyle}>
          {visible ? children : lastChildrenRef.current}
        </div>
      </div>
    </div>
  );
}