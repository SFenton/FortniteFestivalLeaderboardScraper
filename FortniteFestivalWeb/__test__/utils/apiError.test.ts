import { describe, expect, it } from 'vitest';
import {
  getApiErrorStatus,
  isServiceUnavailableError,
  parseApiError,
} from '../../src/utils/apiError';

describe('apiError', () => {
  it('extracts API status codes from Error instances and strings', () => {
    expect(getApiErrorStatus(new Error('API 503: Service Unavailable'))).toBe(503);
    expect(getApiErrorStatus('API 404: Not Found')).toBe(404);
    expect(getApiErrorStatus('network failed')).toBeNull();
  });

  it('classifies only gateway and availability responses as unavailable', () => {
    expect(isServiceUnavailableError(new Error('API 502: Bad Gateway'))).toBe(true);
    expect(isServiceUnavailableError(new Error('API 503: Service Unavailable'))).toBe(true);
    expect(isServiceUnavailableError(new Error('API 504: Gateway Timeout'))).toBe(true);
    expect(isServiceUnavailableError(new Error('API 500: Server Error'))).toBe(false);
  });

  it('keeps the existing translated error category contract', () => {
    expect(parseApiError('API 503: Service Unavailable')).toEqual({
      title: expect.any(String),
      subtitle: expect.any(String),
    });
  });
});
