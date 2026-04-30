import { describe, it, expect } from 'vitest';
import { getBandFilterActionLabel } from '../../src/utils/bandFilterDisplay';

describe('getBandFilterActionLabel', () => {
  it('uses the empty label when no filter is applied', () => {
    expect(getBandFilterActionLabel([], 'Filter Band Type')).toBe('Filter Band Type');
  });

  it('joins selected instrument labels with slashes', () => {
    expect(getBandFilterActionLabel(['Solo_Guitar', 'Solo_Bass', 'Solo_PeripheralVocals'], 'Filter Band Type'))
      .toBe('Lead / Bass / Karaoke');
  });
});
