import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { generateThemeCss } from '../scripts/generate-theme-css.mjs';

const generated = generateThemeCss();
const themeCssPath = path.resolve(__dirname, '../src/styles/theme.css');

describe('theme CSS generator', () => {
  it('keeps the committed CSS synchronized with TypeScript tokens', () => {
    expect(readFileSync(themeCssPath, 'utf8')).toBe(generated);
  });

  it('preserves the stable 115-variable public surface', () => {
    const variableNames = Array.from(generated.matchAll(/^\s+(--[\w-]+):/gm))
      .map(match => match[1]);
    expect(variableNames).toHaveLength(115);
    expect(new Set(variableNames).size).toBe(variableNames.length);
    expect(variableNames).toContain('--color-bg-app');
    expect(variableNames).toContain('--size-icon-instrument-sm');
    expect(variableNames).toContain('--duration-normal');
  });
});
