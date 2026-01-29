import {BatchedLogBuffer} from '../process/logBuffer';

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
});
