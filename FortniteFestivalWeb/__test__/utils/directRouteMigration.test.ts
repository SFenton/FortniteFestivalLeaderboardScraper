import { afterEach, describe, expect, it } from 'vitest';
import { migrateDirectPathToHashRoute } from '../../src/utils/directRouteMigration';

describe('migrateDirectPathToHashRoute', () => {
  afterEach(() => {
    window.history.replaceState(null, '', '/');
  });

  it('moves a direct SPA path and query into the canonical hash route', () => {
    window.history.replaceState({ source: 'test' }, '', '/manual?section=navigation');

    migrateDirectPathToHashRoute();

    expect(window.location.pathname).toBe('/');
    expect(window.location.hash).toBe('#/manual?section=navigation');
    expect(window.history.state).toEqual({ source: 'test' });
  });

  it('preserves an existing hash route', () => {
    window.history.replaceState(null, '', '/manual#/settings');

    migrateDirectPathToHashRoute();

    expect(window.location.pathname).toBe('/manual');
    expect(window.location.hash).toBe('#/settings');
  });

  it.each(['/', '/index.html'])('leaves entry paths unchanged: %s', (path) => {
    window.history.replaceState(null, '', path);

    migrateDirectPathToHashRoute();

    expect(window.location.pathname).toBe(path);
    expect(window.location.hash).toBe('');
  });
});
