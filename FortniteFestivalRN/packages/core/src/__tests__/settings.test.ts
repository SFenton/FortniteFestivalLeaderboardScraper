import {defaultSettings} from '../settings';

describe('settings', () => {
  test('defaultSettings returns expected defaults', () => {
    const s = defaultSettings();
    expect(s.queryLead).toBe(true);
    expect(s.queryDrums).toBe(true);
    expect(s.queryVocals).toBe(true);
    expect(s.queryBass).toBe(true);
    expect(s.queryProLead).toBe(true);
    expect(s.queryProBass).toBe(true);
  });
});
