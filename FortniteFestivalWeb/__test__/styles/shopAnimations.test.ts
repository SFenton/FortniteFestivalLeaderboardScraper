import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const animationsCss = readFileSync(resolve(process.cwd(), 'src/styles/animations.module.css'), 'utf8');

describe('shop animation colors', () => {
  it('uses green for the normal item shop pulse', () => {
    expect(animationsCss).toContain('border: 2px solid var(--color-status-green, #2ECC71);');
    expect(animationsCss).toContain('--shop-pulse-target: var(--color-status-green-stroke, #1E7F46);');
    expect(animationsCss).not.toContain('--shop-pulse-target: var(--color-accent-blue);');
  });

  it('keeps gold and red shop pulse variants distinct', () => {
    expect(animationsCss).toContain('border: 2px solid var(--color-gold, #FFD700);');
    expect(animationsCss).toContain('border: 2px solid var(--color-leaving-red, #ef4444);');
  });
});