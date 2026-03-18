import { describe, it, expect } from 'vitest';
import { APP_VERSION, CORE_VERSION } from '../../hooks/data/useVersions';

describe('useVersions', () => {
  it('exports APP_VERSION as a string', () => {
    expect(typeof APP_VERSION).toBe('string');
  });

  it('exports CORE_VERSION as a string', () => {
    expect(typeof CORE_VERSION).toBe('string');
  });
});
