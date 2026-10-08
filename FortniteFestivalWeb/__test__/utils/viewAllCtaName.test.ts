import { describe, expect, it } from 'vitest';
import { viewAllCtaName } from '../../src/utils/viewAllCtaName';

describe('viewAllCtaName', () => {
  it('starts with the visible label and then names the card', () => {
    expect(viewAllCtaName('View all rivals', 'Lead Rivals')).toBe('View all rivals, Lead Rivals');
  });
});
