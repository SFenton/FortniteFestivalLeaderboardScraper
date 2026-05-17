import { useMemo, type ReactNode, type CSSProperties } from 'react';
import { LoadPhase } from '@festival/core';
import { ZIndex, Layout, PointerEvents, SPINNER_FADE_MS, flexCenter, fixedFill } from '@festival/theme';
import ArcSpinner from '../common/ArcSpinner';

export interface LoadGateProps {
  /** Current load phase from useLoadPhase or manual state. */
  phase: LoadPhase | string;
  /** Fade-out duration for the spinner in ms. Default: SPINNER_FADE_MS. */
  fadeDuration?: number;
  /** If true, spinner is a fixed overlay and children are always rendered below. */
  overlay?: boolean;
  /** Override spinner container class. */
  spinnerClassName?: string;
  /** Extra inline style merged onto the spinner container. */
  spinnerStyle?: CSSProperties;
  /** Optional test id for page-level spinner assertions. */
  spinnerTestId?: string;
  children: ReactNode;
}

const CONTENT_IN: string = LoadPhase.ContentIn;
const SPINNER_OUT: string = LoadPhase.SpinnerOut;

export function LoadGate({
  phase,
  fadeDuration = SPINNER_FADE_MS,
  overlay,
  spinnerClassName,
  spinnerStyle,
  spinnerTestId,
  children,
}: LoadGateProps) {
  const isContentIn = phase === CONTENT_IN;
  const isSpinnerOut = phase === SPINNER_OUT;
  const s = useStyles(!!overlay, isContentIn, isSpinnerOut, fadeDuration);

  const resolvedSpinnerStyle = spinnerClassName
    ? { ...(s.fadeOut ?? {}), ...spinnerStyle }
    : { ...(overlay ? s.spinnerOverlay : s.spinnerContainer), ...spinnerStyle };

  const spinner = (
    <div
      className={spinnerClassName}
      data-testid={spinnerTestId}
      style={resolvedSpinnerStyle}
    >
      <ArcSpinner />
    </div>
  );

  if (overlay) {
    return (
      <>
        {spinner}
        {children}
      </>
    );
  }

  return (
    <>
      {spinner}
      {isContentIn && children}
    </>
  );
}

function useStyles(overlay: boolean, isContentIn: boolean, isSpinnerOut: boolean, fadeDuration: number) {
  return useMemo(() => {
    const hidden = isContentIn
      ? { opacity: 0, pointerEvents: PointerEvents.none } as const
      : {};
    const spinnerOutHitTest = isSpinnerOut
      ? { pointerEvents: PointerEvents.none } as const
      : {};
    const fadeAnim = isSpinnerOut
      ? { animation: `fadeOut ${fadeDuration}ms ease-out forwards` }
      : {};
    return {
      spinnerOverlay: {
        ...fixedFill,
        zIndex: ZIndex.dropdown,
        ...flexCenter,
        ...fadeAnim,
        ...spinnerOutHitTest,
        ...hidden,
      } as CSSProperties,
      /** Viewport minus shell chrome (header + bottom nav + padding) keeps spinner visually centered. */
      spinnerContainer: {
        ...flexCenter,
        minHeight: `calc(100vh - ${Layout.shellChromeHeight}px)`,
        ...fadeAnim,
        ...spinnerOutHitTest,
        ...hidden,
      } as CSSProperties,
      fadeOut: isContentIn
        ? { opacity: 0, pointerEvents: PointerEvents.none } as CSSProperties
        : isSpinnerOut
          ? { animation: `fadeOut ${fadeDuration}ms ease-out forwards`, pointerEvents: PointerEvents.none } as CSSProperties
          : undefined,
    };
  }, [overlay, isContentIn, isSpinnerOut, fadeDuration]);
}
