import { describe, it, expect } from 'vitest';
import { SAFE_AREA_BOTTOM_VAR, paddingWithSafeAreaBottom } from '../../src/utils/safeAreaStyles';

describe('safeAreaStyles', () => {
  it('caps bottom safe-area padding and falls back to the env variable', () => {
    expect(SAFE_AREA_BOTTOM_VAR).toBe('min(var(--sab, env(safe-area-inset-bottom, 0px)), 34px)');
  });

  it('adds bottom safe-area padding while preserving the side default', () => {
    expect(paddingWithSafeAreaBottom(4, 8, 12)).toBe('4px 8px calc(12px + min(var(--sab, env(safe-area-inset-bottom, 0px)), 34px)) 8px');
  });
});
