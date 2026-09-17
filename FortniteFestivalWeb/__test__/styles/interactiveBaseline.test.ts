import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const indexCss = readFileSync(resolve(process.cwd(), 'src/index.css'), 'utf8');
const focusCss = readFileSync(resolve(process.cwd(), 'src/styles/focusAppearance.module.css'), 'utf8');

describe('interactive mobile feedback baseline', () => {
  it('removes native blue tap wash and whole-element opacity dimming', () => {
    expect(indexCss).toContain('-webkit-tap-highlight-color: transparent;');
    expect(indexCss).toMatch(/html\s*\{[^}]*-webkit-tap-highlight-color:\s*transparent;/);
    expect(indexCss).toMatch(/button\s*\{[^}]*all:\s*unset;[^}]*touch-action:\s*manipulation;/);
    expect(indexCss).not.toContain('-webkit-tap-highlight-color: rgba(76, 125, 255, 0.2);');
    expect(indexCss).not.toContain('opacity: 0.88;');
  });

  it('defines a neutral glass press pulse that preserves focus-visible styling', () => {
    expect(indexCss).toContain('[data-press-pulse]::before');
    expect(indexCss).toContain('@keyframes pressGlassPulse');
    expect(indexCss).toContain('rgba(255, 255, 255, 0.22)');
    expect(indexCss).toContain('button:focus-visible');
    expect(indexCss).toContain("[role='link']:focus-visible");
    expect(indexCss).toContain('outline: 2px solid var(--color-accent-blue-bright);');
  });

  it('limits quiet appearance to controls and structural focus targets outside forced colors', () => {
    expect(focusCss).toContain('@media (forced-colors: none)');
    expect(focusCss).toContain('.root:global([data-fst-quiet-focus])');
    expect(focusCss).toContain('[role="link"]');
    expect(focusCss).toContain('[role="dialog"][tabindex="-1"]');
    expect(focusCss).toContain('[role="alertdialog"][tabindex="-1"]');
    expect(focusCss).not.toContain('!important');
    expect(focusCss).not.toContain('box-shadow');
    expect(focusCss).not.toContain('user-select');
  });
});