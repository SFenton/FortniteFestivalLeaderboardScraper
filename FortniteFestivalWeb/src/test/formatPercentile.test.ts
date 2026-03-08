import { describe, it, expect } from 'vitest';
import { formatPercentile } from '../utils/formatPercentile';

describe('formatPercentile', () => {
  it('clamps values below 1 to Top 1%', () => {
    expect(formatPercentile(0.5)).toBe('Top 1%');
    expect(formatPercentile(0.01)).toBe('Top 1%');
  });

  it('clamps exactly 1 to Top 1%', () => {
    expect(formatPercentile(1)).toBe('Top 1%');
  });

  it('clamps 1.5 to Top 2%', () => {
    expect(formatPercentile(1.5)).toBe('Top 2%');
  });

  it('clamps 2.0 to Top 2%', () => {
    expect(formatPercentile(2.0)).toBe('Top 2%');
  });

  it('clamps 4.99 to Top 5%', () => {
    expect(formatPercentile(4.99)).toBe('Top 5%');
  });

  it('clamps 5.0 to Top 5%', () => {
    expect(formatPercentile(5.0)).toBe('Top 5%');
  });

  it('clamps 5.01 to Top 10%', () => {
    expect(formatPercentile(5.01)).toBe('Top 10%');
  });

  it('clamps 10.0 to Top 10%', () => {
    expect(formatPercentile(10.0)).toBe('Top 10%');
  });

  it('clamps 15.5 to Top 20%', () => {
    expect(formatPercentile(15.5)).toBe('Top 20%');
  });

  it('clamps 75 to Top 80%', () => {
    expect(formatPercentile(75)).toBe('Top 80%');
  });

  it('clamps 100 to Top 100%', () => {
    expect(formatPercentile(100)).toBe('Top 100%');
  });

  it('clamps values above 100 to Top 100%', () => {
    expect(formatPercentile(150)).toBe('Top 100%');
  });
});
