/**
 * Timer helpers for testing load-phase transitions.
 *
 * Pages use a Loading → SpinnerOut → ContentIn state machine with
 * SPINNER_FADE_MS (500ms default) delay between SpinnerOut and ContentIn.
 *
 * Usage:
 *   beforeEach(() => { vi.useFakeTimers(); });
 *   afterEach(() => { vi.useRealTimers(); });
 *
 *   // In test:
 *   await advanceThroughLoadPhase();
 */
import { vi } from 'vitest';
import { act } from '@testing-library/react';

const SPINNER_FADE_MS = 500;
const QUICK_FADE_MS = 150;

/**
 * Advance fake timers through the full load phase sequence.
 * Call after data is ready and the component has started its Loading → SpinnerOut transition.
 */
export async function advanceThroughLoadPhase() {
  // SpinnerOut → ContentIn transition
  await act(async () => { vi.advanceTimersByTime(SPINNER_FADE_MS + 50); });
}

/**
 * Advance fake timers through the quick fade variant (LeaderboardPage uses QUICK_FADE_MS).
 */
export async function advanceThroughQuickFade() {
  await act(async () => { vi.advanceTimersByTime(QUICK_FADE_MS + 50); });
}

/**
 * Flush all pending microtasks (Promises) and timers.
 * Useful after mocked API calls resolve.
 */
export async function flushPromisesAndTimers() {
  await act(async () => { await vi.runAllTimersAsync(); });
}

/**
 * Advance the debounce delay (250ms) used by search inputs.
 */
export async function advancePastDebounce() {
  await act(async () => { vi.advanceTimersByTime(300); });
}
