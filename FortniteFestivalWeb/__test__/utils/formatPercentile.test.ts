import { describe, it, expect } from 'vitest';
import { formatPercentileBucket } from '@festival/core/app/formatters';

describe('formatPercentile', () => {
  it('clamps values below 1 to Top 1%', () => {
    expect(formatPercentileBucket(0.5)).toBe('Top 1%');
    expect(formatPercentileBucket(0.01)).toBe('Top 1%');
  });

  it('clamps exactly 1 to Top 1%', () => {
    expect(formatPercentileBucket(1)).toBe('Top 1%');
  });

  it('clamps 1.5 to Top 2%', () => {
    expect(formatPercentileBucket(1.5)).toBe('Top 2%');
  });

  it('clamps 2.0 to Top 2%', () => {
    expect(formatPercentileBucket(2.0)).toBe('Top 2%');
  });

  it('clamps 4.99 to Top 5%', () => {
    expect(formatPercentileBucket(4.99)).toBe('Top 5%');
  });

  it('clamps 5.0 to Top 5%', () => {
    expect(formatPercentileBucket(5.0)).toBe('Top 5%');
  });

  it('clamps 5.01 to Top 10%', () => {
    expect(formatPercentileBucket(5.01)).toBe('Top 10%');
  });

  it('clamps 10.0 to Top 10%', () => {
    expect(formatPercentileBucket(10.0)).toBe('Top 10%');
  });

  it('clamps 15.5 to Top 20%', () => {
    expect(formatPercentileBucket(15.5)).toBe('Top 20%');
  });

  it('clamps 75 to Top 80%', () => {
    expect(formatPercentileBucket(75)).toBe('Top 80%');
  });

  it('clamps 100 to Top 100%', () => {
    expect(formatPercentileBucket(100)).toBe('Top 100%');
  });

  it('clamps values above 100 to Top 100%', () => {
    expect(formatPercentileBucket(150)).toBe('Top 100%');
  });
});
