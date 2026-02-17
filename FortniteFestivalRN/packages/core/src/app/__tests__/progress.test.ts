import {computeProgressState} from '../progress';

describe('app/process/progress', () => {
  test('formats progress label and pct', () => {
    const s = computeProgressState({current: 1, total: 4, started: true, logCounter: 0});
    expect(s.progressPct).toBe(25);
    expect(s.progressLabel).toBe('1/4 (25.0%)');
    expect(s.shouldLog).toBe(false);
    expect(s.nextLogCounter).toBe(0);
  });

  test('logs on first completion, every 25, and on last', () => {
    let counter = 0;

    // started=false increments counter
    let s = computeProgressState({current: 1, total: 100, started: false, logCounter: counter});
    counter = s.nextLogCounter;
    expect(s.shouldLog).toBe(true); // first

    // Next 23 do not
    for (let i = 2; i <= 24; i++) {
      s = computeProgressState({current: i, total: 100, started: false, logCounter: counter});
      counter = s.nextLogCounter;
      expect(s.shouldLog).toBe(false);
    }

    // 25th should log
    s = computeProgressState({current: 25, total: 100, started: false, logCounter: counter});
    counter = s.nextLogCounter;
    expect(s.shouldLog).toBe(true);

    // last should log and reset counter
    s = computeProgressState({current: 100, total: 100, started: false, logCounter: counter});
    expect(s.shouldLog).toBe(true);
    expect(s.nextLogCounter).toBe(0);
  });

  test('returns 0% label when total is not positive', () => {
    const s = computeProgressState({current: 10, total: 0, started: false, logCounter: 0});
    expect(s.progressLabel).toBe('0%');
    expect(s.progressPct).toBe(0);
  });

  test('formatOneDecimal returns 0.0 for NaN via label', () => {
    const s = computeProgressState({current: NaN, total: 4, started: true, logCounter: 0});
    // pct = NaN/4 * 100 = NaN → formatOneDecimal(NaN) → '0.0'
    expect(s.progressLabel).toBe('NaN/4 (0.0%)');
  });

  test('formatOneDecimal returns 0.0 for Infinity via label', () => {
    const s = computeProgressState({current: Infinity, total: 4, started: true, logCounter: 0});
    expect(s.progressLabel).toContain('0.0%');
  });

  test('clamps progressPct to 100 max', () => {
    const s = computeProgressState({current: 10, total: 4, started: true, logCounter: 0});
    expect(s.progressPct).toBe(100);
  });

  test('resets counter when current equals total', () => {
    const s = computeProgressState({current: 5, total: 5, started: true, logCounter: 10});
    expect(s.nextLogCounter).toBe(0);
  });
});
