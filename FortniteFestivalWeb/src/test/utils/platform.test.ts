import { describe, it, expect } from 'vitest';
import { IS_IOS, IS_ANDROID, IS_PWA, IS_MOBILE_DEVICE } from '@festival/ui-utils';

describe('platform detection', () => {
  it('exports boolean values', () => {
    expect(typeof IS_IOS).toBe('boolean');
    expect(typeof IS_ANDROID).toBe('boolean');
    expect(typeof IS_PWA).toBe('boolean');
    expect(typeof IS_MOBILE_DEVICE).toBe('boolean');
  });

  it('IS_MOBILE_DEVICE is IS_IOS || IS_ANDROID', () => {
    expect(IS_MOBILE_DEVICE).toBe(IS_IOS || IS_ANDROID);
  });

  // In jsdom these should all be false
  it('all false in jsdom environment', () => {
    expect(IS_IOS).toBe(false);
    expect(IS_ANDROID).toBe(false);
    expect(IS_PWA).toBe(false);
  });
});
