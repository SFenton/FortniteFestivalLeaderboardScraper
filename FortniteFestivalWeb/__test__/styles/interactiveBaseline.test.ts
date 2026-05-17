import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const indexCss = readFileSync(resolve(process.cwd(), 'src/index.css'), 'utf8');

describe('interactive mobile feedback baseline', () => {
  it('removes native blue tap wash and whole-element opacity dimming', () => {
    expect(indexCss).toContain('-webkit-tap-highlight-color: transparent;');
    expect(indexCss).not.toContain('-webkit-tap-highlight-color: rgba(76, 125, 255, 0.2);');
    expect(indexCss).not.toContain('opacity: 0.88;');
  });

  it('defines a neutral glass press pulse that preserves focus-visible styling', () => {
    expect(indexCss).toContain('[data-press-pulse]::before');
    expect(indexCss).toContain('@keyframes pressGlassPulse');
    expect(indexCss).toContain('rgba(255, 255, 255, 0.22)');
    expect(indexCss).toContain('button:focus-visible');
    expect(indexCss).toContain('outline: 2px solid var(--color-accent-blue-bright);');
  });
});