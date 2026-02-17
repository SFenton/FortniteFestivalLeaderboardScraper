export const findIndexBy = <T>(
  items: ReadonlyArray<T>,
  predicate: (item: T) => boolean,
): number => {
  for (let i = 0; i < items.length; i++) {
    if (predicate(items[i])) return i;
  }
  return -1;
};
