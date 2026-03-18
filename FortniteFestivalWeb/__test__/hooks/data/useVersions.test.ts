import { describe, it, expect, vi } from 'vitest';
import { APP_VERSION, CORE_VERSION } from '../../../src/hooks/data/useVersions';

describe('useVersions', () => {
  it('exports APP_VERSION as a string', () => {
    expect(typeof APP_VERSION).toBe('string');
  });

  it('exports CORE_VERSION as a string', () => {
    expect(typeof CORE_VERSION).toBe('string');
  });
});

describe('useVersions — defined globals branch', () => {
  it('uses defined __APP_VERSION__ when available', async () => {
    vi.resetModules();
    (globalThis as any).__APP_VERSION__ = '2.5.0';
    (globalThis as any).__CORE_VERSION__ = '1.3.0';
    try {
      const mod = await import('../../../src/hooks/data/useVersions');
      expect(mod.APP_VERSION).toBe('2.5.0');
      expect(mod.CORE_VERSION).toBe('1.3.0');
    } finally {
      delete (globalThis as any).__APP_VERSION__;
      delete (globalThis as any).__CORE_VERSION__;
    }
  });
});
