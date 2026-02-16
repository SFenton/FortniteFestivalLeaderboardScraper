import {createLimiter} from '../concurrency';

const delay = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms));

describe('createLimiter', () => {
  test('limits concurrency', async () => {
    const limiter = createLimiter(2);
    let active = 0;
    let maxActive = 0;

    const work = async (ms: number) => {
      active++;
      maxActive = Math.max(maxActive, active);
      await delay(ms);
      active--;
      return ms;
    };

    const tasks = [50, 50, 50, 50, 50].map(ms => limiter.schedule(() => work(ms)));
    const res = await Promise.all(tasks);
    expect(res).toHaveLength(5);
    expect(maxActive).toBeLessThanOrEqual(2);
  });

  test('active and queued getters reflect state', async () => {
    const limiter = createLimiter(1);
    let resolve1!: () => void;
    const p1 = new Promise<void>(r => { resolve1 = r; });

    const t1 = limiter.schedule(() => p1);
    // t1 is running, so active=1
    expect(limiter.active).toBe(1);

    // Schedule another task — it should be queued
    const t2 = limiter.schedule(() => Promise.resolve());
    expect(limiter.queued).toBe(1);

    resolve1();
    await t1;
    await t2;
    // Allow microtask to settle
    await new Promise<void>(r => setTimeout(r, 0));

    expect(limiter.active).toBe(0);
    expect(limiter.queued).toBe(0);
  });

  test('maxConcurrency floors to 1', async () => {
    const limiter = createLimiter(0);
    const result = await limiter.schedule(() => Promise.resolve(42));
    expect(result).toBe(42);
  });
});
