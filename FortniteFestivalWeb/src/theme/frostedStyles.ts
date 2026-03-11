import { Colors } from './colors';

/**
 * Tiny SVG noise texture encoded as a data-URI.
 * Uses an SVG feTurbulence filter to generate fine grain — renders sharp at any
 * scale and weighs ~250 bytes (no external asset needed).
 */
const noiseSvg = `<svg xmlns='http://www.w3.org/2000/svg' width='200' height='200'><filter id='n'><feTurbulence type='fractalNoise' baseFrequency='0.75' numOctaves='4' stitchTiles='stitch'/><feColorMatrix type='saturate' values='0'/></filter><rect width='100%' height='100%' filter='url(%23n)' opacity='0.4'/></svg>`;

const noiseUrl = `url("data:image/svg+xml,${encodeURIComponent(noiseSvg)}")`;

/**
 * Frosted-card style mixin — a glass-like surface that does NOT use
 * `backdrop-filter`.  This means the element:
 *
 * 1. Won't break when a parent applies `mask-image` (scroll fading).
 * 2. Is cheaper to composite (no blur pass on every paint).
 *
 * Apply via spread: `style={{ ...frostedCard, borderRadius: Radius.md }}`
 */
export const frostedCard = {
  backgroundColor: Colors.surfaceFrosted,
  backgroundImage: noiseUrl,
  backgroundRepeat: 'repeat',
  border: `1px solid ${Colors.glassBorder}`,
  boxShadow: [
    'inset 0 1px 0 rgba(255,255,255,0.06)',
    'inset 0 0 30px rgba(255,255,255,0.02)',
    '0 4px 20px rgba(0,0,0,0.4)',
  ].join(', '),
} as const;
