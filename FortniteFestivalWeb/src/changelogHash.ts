import type { ChangelogEntry } from './changelog';

const CURRENT_CHANGELOG_HASH = '-6p8bh3';

export function changelogHash(): string {
  return CURRENT_CHANGELOG_HASH;
}

export function calculateChangelogHash(entries: readonly ChangelogEntry[]): string {
  const value = JSON.stringify(entries);
  let hash = 0;
  for (let index = 0; index < value.length; index += 1) {
    hash = ((hash << 5) - hash) + value.charCodeAt(index);
    hash |= 0;
  }
  return hash.toString(36);
}
