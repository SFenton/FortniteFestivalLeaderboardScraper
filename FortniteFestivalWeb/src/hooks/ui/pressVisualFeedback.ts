import type { PointerEvent as ReactPointerEvent } from 'react';

const PRESS_PULSE_DURATION_MS = 420;
const PRESS_PULSE_ATTRIBUTE = 'data-press-pulse';
const pressPulseTimeouts = new WeakMap<HTMLElement, ReturnType<typeof window.setTimeout>>();

function finishPressPulse(target: HTMLElement) {
  target.removeAttribute(PRESS_PULSE_ATTRIBUTE);
  target.style.removeProperty('--press-pulse-x');
  target.style.removeProperty('--press-pulse-y');
  target.style.removeProperty('--press-pulse-size');
  target.style.removeProperty('--press-pulse-mid-size');
  pressPulseTimeouts.delete(target);
}

export function startPressPulse(event: ReactPointerEvent<Element>) {
  if (typeof window === 'undefined' || typeof HTMLElement === 'undefined') return null;
  if (!(event.currentTarget instanceof HTMLElement)) return null;

  const target = event.currentTarget;
  const rect = target.getBoundingClientRect();
  const fallbackX = rect.width / 2;
  const fallbackY = rect.height / 2;
  const clientX = Number.isFinite(event.clientX) ? event.clientX : rect.left + fallbackX;
  const clientY = Number.isFinite(event.clientY) ? event.clientY : rect.top + fallbackY;
  const localX = Math.min(Math.max(clientX - rect.left, 0), rect.width || fallbackX);
  const localY = Math.min(Math.max(clientY - rect.top, 0), rect.height || fallbackY);
  const pulseSize = Math.round(Math.max(rect.width, rect.height, 48) * 1.8);
  const pulseMidSize = Math.round(pulseSize * 0.52);
  const existingTimeout = pressPulseTimeouts.get(target);

  if (existingTimeout !== undefined) window.clearTimeout(existingTimeout);
  if (target.hasAttribute(PRESS_PULSE_ATTRIBUTE)) {
    target.removeAttribute(PRESS_PULSE_ATTRIBUTE);
    void target.offsetWidth;
  }

  target.style.setProperty('--press-pulse-x', `${localX}px`);
  target.style.setProperty('--press-pulse-y', `${localY}px`);
  target.style.setProperty('--press-pulse-size', `${pulseSize}px`);
  target.style.setProperty('--press-pulse-mid-size', `${pulseMidSize}px`);
  target.setAttribute(PRESS_PULSE_ATTRIBUTE, 'true');

  const timeout = window.setTimeout(() => finishPressPulse(target), PRESS_PULSE_DURATION_MS);
  pressPulseTimeouts.set(target, timeout);
  return target;
}

export function clearPressPulse(target: Element | null) {
  if (typeof window === 'undefined' || typeof HTMLElement === 'undefined') return;
  if (!(target instanceof HTMLElement)) return;

  const existingTimeout = pressPulseTimeouts.get(target);
  if (existingTimeout !== undefined) window.clearTimeout(existingTimeout);
  finishPressPulse(target);
}