export type Task<T> = () => Promise<T>;

export const createLimiter = (maxConcurrency: number) => {
  const concurrency = Math.max(1, Math.floor(maxConcurrency));
  let active = 0;
  const queue: Array<() => void> = [];

  const next = () => {
    if (active >= concurrency) return;
    const run = queue.shift();
    if (!run) return;
    active++;
    run();
  };

  const schedule = <T>(task: Task<T>): Promise<T> =>
    new Promise<T>((resolve, reject) => {
      queue.push(() => {
        task()
          .then(resolve)
          .catch(reject)
          .finally(() => {
            active--;
            next();
          });
      });
      next();
    });

  return {schedule, get active() { return active; }, get queued() { return queue.length; }};
};
