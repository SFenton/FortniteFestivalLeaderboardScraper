import {BatchedLogBuffer} from '../logBuffer';

describe('app/process/logBuffer', () => {
  test('flush is a no-op when pending is empty', () => {
    const buf = new BatchedLogBuffer({maxLines: 3, maxPending: 5, trimPendingTo: 4});
    const r = buf.flush('\n');
    expect(r.added).toEqual([]);
    expect(r.lines).toEqual([]);
    expect(r.joined).toBe('');
  });

  test('enqueues and flushes; trims visible lines to maxLines', () => {
    const buf = new BatchedLogBuffer({maxLines: 3, maxPending: 10, trimPendingTo: 8});
    buf.enqueue('a');
    buf.enqueue('b');
    buf.enqueue('c');
    expect(buf.flush('\n').lines).toEqual(['a', 'b', 'c']);

    buf.enqueue('d');
    buf.enqueue('e');
    const r2 = buf.flush('\n');
    expect(r2.lines).toEqual(['c', 'd', 'e']);
    expect(r2.joined).toBe('c\nd\ne');
  });

  test('trims pending queue to trimPendingTo when it exceeds maxPending', () => {
    const buf = new BatchedLogBuffer({maxLines: 100, maxPending: 5, trimPendingTo: 3});

    buf.enqueue('1');
    buf.enqueue('2');
    buf.enqueue('3');
    buf.enqueue('4');
    buf.enqueue('5');
    buf.enqueue('6'); // exceeds maxPending -> keep last 3

    const r = buf.flush('\n');
    expect(r.added).toEqual(['4', '5', '6']);
  });

  test('snapshot returns a copy of the current visible lines', () => {
    const buf = new BatchedLogBuffer({maxLines: 10, maxPending: 10, trimPendingTo: 8});
    buf.enqueue('x');
    buf.enqueue('y');
    buf.flush('\n');
    const snap = buf.snapshot();
    expect(snap).toEqual(['x', 'y']);
    // Mutations to snap don't affect the buffer
    snap.push('z');
    expect(buf.snapshot()).toEqual(['x', 'y']);
  });

  test('constructor validates maxLines > 0', () => {
    expect(() => new BatchedLogBuffer({maxLines: 0})).toThrow('maxLines must be > 0');
  });

  test('constructor validates maxPending > 0', () => {
    expect(() => new BatchedLogBuffer({maxPending: 0})).toThrow('maxPending must be > 0');
  });

  test('constructor validates trimPendingTo > 0', () => {
    expect(() => new BatchedLogBuffer({trimPendingTo: 0})).toThrow('trimPendingTo must be > 0');
  });

  test('constructor validates trimPendingTo <= maxPending', () => {
    expect(() => new BatchedLogBuffer({maxPending: 5, trimPendingTo: 10})).toThrow('trimPendingTo must be <= maxPending');
  });

  test('constructor uses default config values when config is empty', () => {
    // Calling with {} triggers ?? defaults: maxLines=4000, maxPending=2000, trimPendingTo=1500
    const buf = new BatchedLogBuffer({});
    // Demonstrate it works with defaults by enqueueing and flushing
    buf.enqueue('test');
    const r = buf.flush();
    expect(r.lines).toEqual(['test']);
  });

  test('constructor uses default config when no argument passed', () => {
    // Calling with no arg triggers config={} then ?? defaults
    const buf = new BatchedLogBuffer();
    buf.enqueue('a');
    const r = buf.flush();
    expect(r.lines).toEqual(['a']);
  });

  test('flush uses default newline parameter', () => {
    const buf = new BatchedLogBuffer({maxLines: 10, maxPending: 10, trimPendingTo: 8});
    buf.enqueue('a');
    buf.enqueue('b');
    const r = buf.flush(); // default newline = '\n'
    expect(r.joined).toBe('a\nb');
  });
});
