import { describe, expect, it } from 'vitest';
import { comboScopeLabel, rankingScopeLabel, resolveSupportedRankingScopes } from '../../src/utils/rankingScopes';

describe('rankingScopes', () => {
  it('splits cross-group selections into supported combo scopes', () => {
    const scopes = resolveSupportedRankingScopes([
      'Solo_Guitar',
      'Solo_Drums',
      'Solo_PeripheralVocals',
      'Solo_PeripheralCymbals',
    ]);

    expect(scopes).toEqual([
      {
        kind: 'combo',
        family: 'og-band',
        instruments: ['Solo_Guitar', 'Solo_Drums'],
        comboId: '05',
        queryValue: '05',
        scopeKey: '05',
      },
      {
        kind: 'combo',
        family: 'peripherals',
        instruments: ['Solo_PeripheralVocals', 'Solo_PeripheralCymbals'],
        comboId: 'c0',
        queryValue: 'c0',
        scopeKey: 'c0',
      },
    ]);
  });

  it('keeps single-instrument family selections as single scopes', () => {
    const scopes = resolveSupportedRankingScopes([
      'Solo_Guitar',
      'Solo_Drums',
      'Solo_PeripheralCymbals',
    ]);

    expect(scopes).toEqual([
      {
        kind: 'combo',
        family: 'og-band',
        instruments: ['Solo_Guitar', 'Solo_Drums'],
        comboId: '05',
        queryValue: '05',
        scopeKey: '05',
      },
      {
        kind: 'instrument',
        family: 'peripherals',
        instruments: ['Solo_PeripheralCymbals'],
        instrument: 'Solo_PeripheralCymbals',
        queryValue: 'Solo_PeripheralCymbals',
        scopeKey: 'Solo_PeripheralCymbals',
      },
    ]);
  });

  it('formats labels from scope instruments and combo ids', () => {
    const [comboScope] = resolveSupportedRankingScopes(['Solo_Guitar', 'Solo_Drums']);
    const [, singleScope] = resolveSupportedRankingScopes(['Solo_Guitar', 'Solo_Drums', 'Solo_PeripheralDrums']);

    expect(rankingScopeLabel(comboScope)).toBe('Lead + Drums');
    expect(rankingScopeLabel(singleScope!)).toBe('Pro Drums');
    expect(comboScopeLabel('c0')).toBe('Karaoke + Pro Drums + Cymbals');
  });
});
