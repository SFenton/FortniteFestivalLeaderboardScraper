import {findIndexBy} from '../findIndexBy';

describe('findIndexBy', () => {
  it('returns the first matching index', () => {
    const items = [{id: 1}, {id: 2}, {id: 3}, {id: 2}];
    expect(findIndexBy(items, x => x.id === 2)).toBe(1);
  });

  it('returns -1 when no match exists', () => {
    expect(findIndexBy([1, 2, 3], x => x === 9)).toBe(-1);
  });

  it('returns -1 for an empty array', () => {
    expect(findIndexBy([], () => true)).toBe(-1);
  });
});
