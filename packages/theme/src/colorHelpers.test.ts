import { describe, expect, it } from 'vitest';
import { ACCURACY_GRADIENT, accuracyColor } from './colorHelpers';

describe('accuracy color helpers', () => {
  it('preserves the canonical red-to-green endpoints', () => {
    expect(accuracyColor(0)).toBe('rgb(220,40,40)');
    expect(accuracyColor(100)).toBe('rgb(46,204,113)');
    expect(ACCURACY_GRADIENT).toBe(
      'linear-gradient(to right, rgb(220,40,40), rgb(46,204,113))',
    );
  });

  it('clamps and interpolates percentages', () => {
    expect(accuracyColor(-50)).toBe(accuracyColor(0));
    expect(accuracyColor(150)).toBe(accuracyColor(100));
    expect(accuracyColor(50)).toBe('rgb(133,122,77)');
  });
});
