import { describe, it, expect, vi } from 'vitest';
import { APP_VERSION, APP_COMMIT, APP_VERSION_LABEL, CORE_VERSION, formatAppVersionLabel } from '../../../src/hooks/data/useVersions';
import { resolveAppCommit, resolveAppVersion } from '../../../scripts/app-version.mjs';

describe('useVersions', () => {
  it('exports APP_VERSION as a string', () => {
    expect(typeof APP_VERSION).toBe('string');
  });

  it('exports CORE_VERSION as a string', () => {
    expect(typeof CORE_VERSION).toBe('string');
  });

  it('labels the app version with the build commit when one is stamped', () => {
    expect(APP_VERSION_LABEL).toBe(formatAppVersionLabel(APP_VERSION, APP_COMMIT));
    expect(formatAppVersionLabel('0.1.1604', 'abc1234')).toBe('0.1.1604 · abc1234');
    expect(formatAppVersionLabel('0.1.135', '')).toBe('0.1.135');
  });
});

describe('useVersions — defined globals branch', () => {
  it('uses defined __APP_VERSION__ and __APP_COMMIT__ when available', async () => {
    vi.resetModules();
    (globalThis as any).__APP_VERSION__ = '2.5.0';
    (globalThis as any).__APP_COMMIT__ = '598965e';
    (globalThis as any).__CORE_VERSION__ = '1.3.0';
    try {
      const mod = await import('../../../src/hooks/data/useVersions');
      expect(mod.APP_VERSION).toBe('2.5.0');
      expect(mod.APP_COMMIT).toBe('598965e');
      expect(mod.APP_VERSION_LABEL).toBe('2.5.0 · 598965e');
      expect(mod.CORE_VERSION).toBe('1.3.0');
    } finally {
      delete (globalThis as any).__APP_VERSION__;
      delete (globalThis as any).__APP_COMMIT__;
      delete (globalThis as any).__CORE_VERSION__;
    }
  });
});

describe('build-time app version resolution', () => {
  it('replaces the package patch with the master build number', () => {
    expect(resolveAppVersion('0.1.135', '1603')).toBe('0.1.1603');
    expect(resolveAppVersion('2.4.9-beta.1', '1700')).toBe('2.4.1700');
  });

  it('gives consecutive master merges distinct increasing versions', () => {
    const first = resolveAppVersion('0.1.135', '1603').split('.').map(Number);
    const second = resolveAppVersion('0.1.135', '1604').split('.').map(Number);
    expect(second).not.toEqual(first);
    expect(second[2]).toBeGreaterThan(first[2]!);
  });

  it('keeps the package version for local builds or malformed build numbers', () => {
    for (const build of [undefined, '', '0', '-3', '12a', '1.5', ' ']) {
      expect(resolveAppVersion('0.1.135', build)).toBe('0.1.135');
    }
    expect(resolveAppVersion('not-semver', '1603')).toBe('not-semver');
    expect(resolveAppVersion('0.1.135', ' 1603\n')).toBe('0.1.1603');
  });

  it('shortens a stamped commit and drops missing or invalid values', () => {
    expect(resolveAppCommit('598965EF0123456789abcdef0123456789abcdef')).toBe('598965e');
    expect(resolveAppCommit('598965e')).toBe('598965e');
    for (const commit of [undefined, '', 'dev', 'xyz1234', '12345']) {
      expect(resolveAppCommit(commit)).toBe('');
    }
  });
});
