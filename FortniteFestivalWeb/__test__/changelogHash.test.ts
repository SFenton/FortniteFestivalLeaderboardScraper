import { describe, expect, it } from 'vitest';
import { changelog } from '../src/changelog';
import { calculateChangelogHash, changelogHash } from '../src/changelogHash';

describe('changelogHash', () => {
  it('matches the current lazy changelog content', () => {
    expect(changelogHash()).toBe(calculateChangelogHash(changelog));
  });
});
