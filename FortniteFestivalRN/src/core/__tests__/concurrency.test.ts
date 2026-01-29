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
});
