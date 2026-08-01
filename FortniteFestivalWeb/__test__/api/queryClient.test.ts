import { describe, expect, it } from 'vitest';
import { queryClient } from '../../src/api/queryClient';

describe('queryClient retry policy', () => {
  const retry = queryClient.getDefaultOptions().queries?.retry;

  it('does not retry service-unavailable responses', () => {
    expect(typeof retry).toBe('function');
    expect((retry as (count: number, error: Error) => boolean)(
      0,
      new Error('API 503: Service Unavailable'),
    )).toBe(false);
  });

  it('retains one retry for other transient failures', () => {
    expect((retry as (count: number, error: Error) => boolean)(
      0,
      new Error('network failed'),
    )).toBe(true);
    expect((retry as (count: number, error: Error) => boolean)(
      1,
      new Error('network failed'),
    )).toBe(false);
  });
});
