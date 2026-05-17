import type { PointerEvent as ReactPointerEvent } from 'react';

const CLICK_SUPPRESSION_DISTANCE = 24;

type CompatibilityClickSuppression = {
  clientX: number;
  clientY: number;
  timeStamp: number;
  expiresAt: number;
};

let pendingCompatibilityClickSuppression: CompatibilityClickSuppression | null = null;
let compatibilityClickSuppressorInstalled = false;
let compatibilityClickSuppressorTimeout: ReturnType<typeof window.setTimeout> | null = null;

function eventTime(event: { timeStamp: number }) {
  if (event.timeStamp) return event.timeStamp;
  if (typeof performance !== 'undefined') return performance.now();
  return Date.now();
}

function clearCompatibilityClickSuppressor() {
  pendingCompatibilityClickSuppression = null;
  if (compatibilityClickSuppressorTimeout !== null && typeof window !== 'undefined') {
    window.clearTimeout(compatibilityClickSuppressorTimeout);
    compatibilityClickSuppressorTimeout = null;
  }
  if (!compatibilityClickSuppressorInstalled || typeof document === 'undefined') return;
  document.removeEventListener('click', suppressCompatibilityClick, true);
  compatibilityClickSuppressorInstalled = false;
}

function suppressCompatibilityClick(event: MouseEvent) {
  const suppression = pendingCompatibilityClickSuppression;
  if (!suppression) {
    clearCompatibilityClickSuppressor();
    return;
  }

  const clickTime = eventTime(event);
  if (clickTime > suppression.expiresAt) {
    clearCompatibilityClickSuppressor();
    return;
  }

  const moved = Math.hypot(event.clientX - suppression.clientX, event.clientY - suppression.clientY);
  clearCompatibilityClickSuppressor();
  if (clickTime >= suppression.timeStamp && moved <= CLICK_SUPPRESSION_DISTANCE) {
    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation();
  }
}

export function scheduleCompatibilityClickSuppression(event: ReactPointerEvent<Element>, clickSuppressionMs: number) {
  if (typeof document === 'undefined' || typeof window === 'undefined') return;
  const timeStamp = eventTime(event);
  pendingCompatibilityClickSuppression = {
    clientX: event.clientX,
    clientY: event.clientY,
    timeStamp,
    expiresAt: timeStamp + clickSuppressionMs,
  };

  if (!compatibilityClickSuppressorInstalled) {
    document.addEventListener('click', suppressCompatibilityClick, true);
    compatibilityClickSuppressorInstalled = true;
  }
  if (compatibilityClickSuppressorTimeout !== null) window.clearTimeout(compatibilityClickSuppressorTimeout);
  compatibilityClickSuppressorTimeout = window.setTimeout(clearCompatibilityClickSuppressor, clickSuppressionMs + 50);
}