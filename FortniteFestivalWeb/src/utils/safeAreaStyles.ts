export const SAFE_AREA_TOP_ENV_VAR = 'env(safe-area-inset-top, 0px)';
export const SAFE_AREA_TOP_RAW_VAR = `var(--sat, ${SAFE_AREA_TOP_ENV_VAR})`;
export const SAFE_AREA_TOP_VAR = SAFE_AREA_TOP_RAW_VAR;
export const SAFE_AREA_BOTTOM_ENV_VAR = 'env(safe-area-inset-bottom, 0px)';
export const SAFE_AREA_BOTTOM_RAW_VAR = `var(--sab, ${SAFE_AREA_BOTTOM_ENV_VAR})`;
export const SAFE_AREA_BOTTOM_VAR = `min(${SAFE_AREA_BOTTOM_RAW_VAR}, 34px)`;

export function paddingWithSafeAreaTop(top: number, right: number, bottom: number, left = right): string {
  return `calc(${top}px + ${SAFE_AREA_TOP_VAR}) ${right}px ${bottom}px ${left}px`;
}

export function safeAreaBottomOffset(basePx: number): string {
  return `calc(${basePx}px + ${SAFE_AREA_BOTTOM_VAR})`;
}

export function paddingWithSafeAreaBottom(top: number, right: number, bottom: number, left = right): string {
  return `${top}px ${right}px calc(${bottom}px + ${SAFE_AREA_BOTTOM_VAR}) ${left}px`;
}

export function readSafeAreaBottomPx(): number {
  if (typeof document === 'undefined') return 0;

  const probe = document.createElement('div');
  probe.style.position = 'fixed';
  probe.style.top = '0';
  probe.style.left = '0';
  probe.style.width = '0';
  probe.style.height = `min(${SAFE_AREA_BOTTOM_ENV_VAR}, 34px)`;
  probe.style.visibility = 'hidden';
  probe.style.pointerEvents = 'none';
  document.body.appendChild(probe);

  const value = probe.offsetHeight;
  probe.remove();

  return Number.isFinite(value) ? value : 0;
}
