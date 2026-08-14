#!/usr/bin/env node

import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(scriptDir, '..');
const themeRoot = path.resolve(webRoot, '../packages/theme/src');
const outputPath = path.join(webRoot, 'src/styles/theme.css');
const expectedTokenCount = 115;

const sources = {
  Colors: readObjectTokens('colors.ts', 'Colors'),
  Radius: readObjectTokens('spacing.ts', 'Radius'),
  Font: readObjectTokens('spacing.ts', 'Font'),
  Weight: readObjectTokens('spacing.ts', 'Weight'),
  ZIndex: readObjectTokens('spacing.ts', 'ZIndex'),
  LineHeight: readObjectTokens('spacing.ts', 'LineHeight'),
  Gap: readObjectTokens('spacing.ts', 'Gap'),
  Opacity: readObjectTokens('spacing.ts', 'Opacity'),
  IconSize: readObjectTokens('spacing.ts', 'IconSize'),
  InstrumentSize: readObjectTokens('spacing.ts', 'InstrumentSize'),
  AlbumArtSize: readObjectTokens('spacing.ts', 'AlbumArtSize'),
  MetadataSize: readObjectTokens('spacing.ts', 'MetadataSize'),
  GeneralSize: readObjectTokens('spacing.ts', 'GeneralSize'),
  MaxWidth: readObjectTokens('spacing.ts', 'MaxWidth'),
  Layout: readObjectTokens('spacing.ts', 'Layout'),
  Animation: readExportedConstants('animation.ts'),
};

const sections = [
  section('Backgrounds', [
    token('--color-bg-app', 'Colors', 'backgroundApp'),
    token('--color-bg-card', 'Colors', 'backgroundCard'),
    token('--color-bg-black', 'Colors', 'backgroundBlack'),
    token('--color-bg-card-alt', 'Colors', 'backgroundCardAlt'),
    token('--color-bg-card-alt2', 'Colors', 'backgroundCardAlt2'),
  ]),
  section('Surfaces', [
    token('--color-surface-frosted', 'Colors', 'surfaceFrosted'),
    token('--color-surface-elevated', 'Colors', 'surfaceElevated'),
    token('--color-surface-subtle', 'Colors', 'surfaceSubtle'),
    token('--color-surface-pressed', 'Colors', 'surfacePressed'),
    token('--color-surface-muted', 'Colors', 'surfaceMuted'),
  ]),
  section('Frosted glass', [
    token('--color-glass-card', 'Colors', 'glassCard'),
    token('--color-glass-border', 'Colors', 'glassBorder'),
    token('--color-glass-nav', 'Colors', 'glassNav'),
    token('--glow-color', 'Colors', 'glowHighlight'),
    token('--glow-size', 'GeneralSize', 'glow', 'px'),
  ]),
  section('Overlays', [
    token('--color-overlay-modal', 'Colors', 'overlayModal'),
    token('--color-overlay-scrim', 'Colors', 'overlayScrim'),
    token('--color-overlay-dark', 'Colors', 'overlayDark'),
  ]),
  section('Text', [
    token('--color-text-primary', 'Colors', 'textPrimary'),
    token('--color-text-secondary', 'Colors', 'textSecondary'),
    token('--color-text-tertiary', 'Colors', 'textTertiary'),
    token('--color-text-muted', 'Colors', 'textMuted'),
    token('--color-text-subtle', 'Colors', 'textSubtle'),
    token('--color-text-disabled', 'Colors', 'textDisabled'),
    token('--color-text-placeholder', 'Colors', 'textPlaceholder'),
  ]),
  section('Borders', [
    token('--color-border-primary', 'Colors', 'borderPrimary'),
    token('--color-border-card', 'Colors', 'borderCard'),
    token('--color-border-subtle', 'Colors', 'borderSubtle'),
    token('--color-border-separator', 'Colors', 'borderSeparator'),
  ]),
  section('Accent / Brand', [
    token('--color-accent-blue', 'Colors', 'accentBlue'),
    token('--color-accent-blue-bright', 'Colors', 'accentBlueBright'),
    token('--color-accent-purple', 'Colors', 'accentPurple'),
    token('--color-accent-purple-dark', 'Colors', 'accentPurpleDark'),
  ]),
  section('Gold / FC', [
    token('--color-gold', 'Colors', 'gold'),
    token('--color-gold-bg', 'Colors', 'goldBg'),
    token('--color-gold-stroke', 'Colors', 'goldStroke'),
  ]),
  section('Status', [
    token('--color-status-green', 'Colors', 'statusGreen'),
    token('--color-status-green-stroke', 'Colors', 'statusGreenStroke'),
    token('--color-status-red', 'Colors', 'statusRed'),
    token('--color-status-red-stroke', 'Colors', 'statusRedStroke'),
    token('--color-danger-bg', 'Colors', 'dangerBg'),
    token('--color-chip-selected', 'Colors', 'chipSelected'),
    token('--color-purple-tab-active', 'Colors', 'purpleTabActive'),
  ]),
  section('Spacing', [
    token('--gap-xs', 'Gap', 'xs', 'px'),
    token('--gap-sm', 'Gap', 'sm', 'px'),
    token('--gap-md', 'Gap', 'md', 'px'),
    token('--gap-lg', 'Gap', 'lg', 'px'),
    token('--gap-xl', 'Gap', 'xl', 'px'),
    token('--gap-section', 'Gap', 'section', 'px'),
  ]),
  section('Radius', [
    token('--radius-xs', 'Radius', 'xs', 'px'),
    token('--radius-sm', 'Radius', 'sm', 'px'),
    token('--radius-md', 'Radius', 'md', 'px'),
    token('--radius-lg', 'Radius', 'lg', 'px'),
    token('--radius-full', 'Radius', 'full', 'px'),
  ]),
  section('Font', [
    token('--font-xs', 'Font', 'xs', 'px'),
    token('--font-sm', 'Font', 'sm', 'px'),
    token('--font-md', 'Font', 'md', 'px'),
    token('--font-lg', 'Font', 'lg', 'px'),
    token('--font-xl', 'Font', 'xl', 'px'),
    token('--font-title', 'Font', 'title', 'px'),
    token('--font-2xl', 'Font', '2xl', 'px'),
    token('--font-display', 'Font', 'display', 'px'),
    token('--line-height-tight', 'LineHeight', 'tight'),
  ]),
  section('Size', [
    token('--size-xs', 'IconSize', 'default', 'px'),
    token('--size-sm', 'IconSize', 'sm', 'px'),
    token('--size-md', 'IconSize', 'md', 'px'),
    token('--size-lg', 'IconSize', 'lg', 'px'),
    token('--size-xl', 'IconSize', 'xl', 'px'),
    token('--size-3xl', 'GeneralSize', 'profileCircle', 'px'),
    token('--size-4xl', 'AlbumArtSize', 'collapsed', 'px'),
    token('--size-5xl', 'AlbumArtSize', 'expanded', 'px'),
    token('--size-thumb', 'GeneralSize', 'thumb', 'px'),
    token('--size-icon-lg', 'IconSize', 'lg', 'px'),
    token('--size-icon-instrument-sm', 'InstrumentSize', 'sm', 'px'),
    token('--size-icon-md', 'IconSize', 'md', 'px'),
    token('--size-icon-sm', 'IconSize', 'sm', 'px'),
    token('--size-instrument-chip', 'InstrumentSize', 'chip', 'px'),
    token('--size-button-close', 'Layout', 'buttonCloseSize', 'px'),
    token('--size-button-nav', 'Layout', 'buttonNavSize', 'px'),
    token('--size-dot', 'MetadataSize', 'dotSize', 'px'),
    token('--album-size', 'GeneralSize', 'album', 'px'),
  ]),
  section('Font Weights', [
    token('--weight-semibold', 'Weight', 'semibold'),
    token('--weight-bold', 'Weight', 'bold'),
    token('--weight-heavy', 'Weight', 'heavy'),
  ]),
  section('Layout', [
    token('--layout-padding-h', 'Layout', 'paddingHorizontal', 'px'),
    token('--layout-padding-top', 'Layout', 'paddingTop', 'px'),
    token('--sidebar-width', 'Layout', 'sidebarWidth', 'px'),
    token('--layout-padding-h-pinned', 'Layout', 'paddingHorizontalPinned', 'px'),
    token('--max-width-card', 'MaxWidth', 'card', 'px'),
    token('--max-width-grid', 'MaxWidth', 'grid', 'px'),
    token('--max-width-narrow', 'MaxWidth', 'narrow', 'px'),
    token('--max-width-carousel', 'Layout', 'carouselMaxWidth', 'px'),
  ]),
  section('Carousel', [
    token('--carousel-height', 'Layout', 'carouselHeight'),
    token('--carousel-max-height', 'Layout', 'carouselMaxHeight', 'px'),
    token('--carousel-min-height', 'Layout', 'carouselMinHeight', 'px'),
    token('--carousel-height-mobile', 'Layout', 'carouselHeightMobile'),
    token('--carousel-max-height-mobile', 'Layout', 'carouselMaxHeightMobile', 'px'),
  ]),
  section('Duration', [
    token('--duration-quick', 'Animation', 'QUICK_FADE_MS', 'ms'),
    token('--duration-fast', 'Animation', 'FAST_FADE_MS', 'ms'),
    token('--duration-normal', 'Animation', 'TRANSITION_MS', 'ms'),
    token('--duration-fade', 'Animation', 'FADE_DURATION', 'ms'),
    token('--duration-confirm', 'Animation', 'FAST_FADE_MS', 'ms'),
    token('--duration-bg', 'Animation', 'TRANSITION_MS', 'ms'),
  ]),
  section('Z-Index', [
    token('--z-background', 'ZIndex', 'background'),
    token('--z-base', 'ZIndex', 'base'),
    token('--z-dropdown', 'ZIndex', 'dropdown'),
    token('--z-popover', 'ZIndex', 'popover'),
    token('--z-modal-overlay', 'ZIndex', 'modalOverlay'),
  ]),
  section('Opacity', [
    token('--opacity-muted', 'Opacity', 'disabled'),
  ]),
  section('Colors (additional)', [
    token('--color-bg-card-purple', 'Colors', 'diffExpertBg'),
    token('--color-accent-purple-soft', 'Colors', 'purpleButtonBg'),
    token('--color-purple-border-subtle', 'Colors', 'purpleBorderSubtle'),
    token('--color-purple-border-glass', 'Colors', 'purpleBorderGlass'),
    token('--color-purple-highlight', 'Colors', 'purpleHighlight'),
    token('--color-purple-highlight-border', 'Colors', 'purpleHighlightBorder'),
  ]),
];

export function generateThemeCss() {
  const declarations = sections.flatMap(entry => entry.tokens);
  if (declarations.length !== expectedTokenCount) {
    throw new Error(
      `Theme CSS mapping has ${declarations.length} tokens; expected ${expectedTokenCount}.`,
    );
  }

  const body = sections.map(entry => [
    `  /* ${entry.title} */`,
    ...entry.tokens.map(({ cssName, sourceName, tokenName, unit }) => {
      const value = sources[sourceName]?.get(tokenName);
      if (value === undefined) {
        throw new Error(`Missing ${sourceName}.${tokenName} for ${cssName}.`);
      }
      return `  ${cssName}: ${formatValue(value, unit)};`;
    }),
  ].join('\n')).join('\n\n');

  return [
    '/*',
    ' * AUTO-GENERATED by scripts/generate-theme-css.mjs.',
    ' * Source of truth: packages/theme/src.',
    ' * Run `yarn theme:css:generate` after changing mapped theme tokens.',
    ' */',
    ':root {',
    body,
    '}',
    '',
  ].join('\n');
}

function section(title, tokens) {
  return { title, tokens };
}

function token(cssName, sourceName, tokenName, unit = '') {
  return { cssName, sourceName, tokenName, unit };
}

function formatValue(value, unit) {
  return typeof value === 'number' ? `${value}${unit}` : value;
}

function readObjectTokens(fileName, exportName) {
  const source = readFileSync(path.join(themeRoot, fileName), 'utf8');
  const marker = `export const ${exportName} = {`;
  const start = source.indexOf(marker);
  if (start < 0) throw new Error(`Unable to find ${exportName} in ${fileName}.`);
  const end = source.indexOf('\n} as const;', start);
  if (end < 0) throw new Error(`Unable to find the end of ${exportName} in ${fileName}.`);
  const values = new Map();
  for (const line of source.slice(start + marker.length, end).split('\n')) {
    const match = /^\s*(?:'([^']+)'|([A-Za-z_$][\w$]*)):\s*(.+?),?\s*$/.exec(line);
    if (!match) continue;
    const name = match[1] ?? match[2];
    if (!name) continue;
    const value = parseLiteral(match[3] ?? '', `${exportName}.${name}`);
    if (value !== undefined) values.set(name, value);
  }
  return values;
}

function readExportedConstants(fileName) {
  const source = readFileSync(path.join(themeRoot, fileName), 'utf8');
  const values = new Map();
  for (const match of source.matchAll(
    /^export const ([A-Za-z_$][\w$]*) = (.+?);$/gm,
  )) {
    const name = match[1];
    if (!name) continue;
    const value = parseLiteral(match[2] ?? '', name);
    if (value !== undefined) values.set(name, value);
  }
  return values;
}

function parseLiteral(rawValue, label) {
  const value = rawValue.replace(/\s+as\s+string$/, '').trim();
  const numericValue = value.replaceAll('_', '');
  if (/^-?(?:\d+\.?\d*|\.\d+)$/.test(numericValue)) return Number(numericValue);
  const stringMatch = /^'([^']*)'$/.exec(value);
  if (stringMatch) return stringMatch[1] ?? '';
  if (
    value.startsWith('{')
    || value.startsWith('[')
    || value.includes('.')
  ) {
    return undefined;
  }
  throw new Error(`Unsupported token value for ${label}: ${rawValue}`);
}

function parseArgs(argv) {
  if (argv.length === 0) return { check: false };
  if (argv.length === 1 && argv[0] === '--check') return { check: true };
  throw new Error(`Unknown arguments: ${argv.join(' ')}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const { check } = parseArgs(process.argv.slice(2));
    const generated = generateThemeCss();
    if (check) {
      const current = readFileSync(outputPath, 'utf8');
      if (current !== generated) {
        console.error(
          'Theme CSS is out of date. Run `corepack yarn theme:css:generate`.',
        );
        process.exitCode = 1;
      } else {
        console.log(`[theme-css] ${expectedTokenCount} variables are current.`);
      }
    } else {
      writeFileSync(outputPath, generated);
      console.log(`[theme-css] Wrote ${expectedTokenCount} variables to ${outputPath}.`);
    }
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 64;
  }
}
