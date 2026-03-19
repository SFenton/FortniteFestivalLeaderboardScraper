/**
 * Renders a spinner during loading/spinnerOut phases and children during contentIn.
 *
 * Replaces the repeated pattern across pages:
 *   {phase !== 'contentIn' && <spinner />}
 *   {phase === 'contentIn' && content}
 *
 * @example
 *   <LoadGate phase={phase}>
 *     <div>Main content</div>
 *   </LoadGate>
 *
 * @example overlay spinner (SongsPage, SuggestionsPage)
 *   <LoadGate phase={phase} overlay>
 *     <div>Main content (always in DOM, spinner floats above)</div>
 *   </LoadGate>
 */
import { type ReactNode } from 'react';
import { LoadPhase } from '@festival/core';
import { SPINNER_FADE_MS } from '@festival/theme';
import ArcSpinner from './ArcSpinner';
import css from '../page/LoadGate.module.css';

export interface LoadGateProps {
  /** Current load phase from useLoadPhase or manual state. */
  phase: LoadPhase | string;
  /** Fade-out duration for the spinner in ms. Default: SPINNER_FADE_MS. */
  fadeDuration?: number;
  /** If true, spinner is a fixed overlay and children are always rendered below. */
  overlay?: boolean;
  /** Override spinner container class. */
  spinnerClassName?: string;
  children: ReactNode;
}

const CONTENT_IN: string = LoadPhase.ContentIn;
const SPINNER_OUT: string = LoadPhase.SpinnerOut;

export function LoadGate({
  phase,
  fadeDuration = SPINNER_FADE_MS,
  overlay,
  spinnerClassName,
  children,
}: LoadGateProps) {
  const isContentIn = phase === CONTENT_IN;
  const isSpinnerOut = phase === SPINNER_OUT;

  const spinner = !isContentIn ? (
    <div
      className={spinnerClassName ?? (overlay ? css.spinnerOverlay : css.spinnerContainer)}
      style={isSpinnerOut ? { animation: `fadeOut ${fadeDuration}ms ease-out forwards` } : undefined}
    >
      <ArcSpinner />
    </div>
  ) : null;

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
