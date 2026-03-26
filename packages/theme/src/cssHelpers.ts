/**
 * CSS value builder helpers — eliminates template string construction in useStyles.
 */

/** Build a border shorthand: `"2px solid #CFA500"` */
export function border(width: number, color: string, style = 'solid'): string {
  return `${width}px ${style} ${color}`;
}

/** Build a padding shorthand from numeric values (px). */
export function padding(top: number, right?: number, bottom?: number, left?: number): string {
  if (right == null) return `${top}px`;
  if (bottom == null) return `${top}px ${right}px`;
  if (left == null) return `${top}px ${right}px ${bottom}px`;
  return `${top}px ${right}px ${bottom}px ${left}px`;
}

/** Build a margin shorthand from numeric values (px). */
export function margin(top: number, right?: number, bottom?: number, left?: number): string {
  if (right == null) return `${top}px`;
  if (bottom == null) return `${top}px ${right}px`;
  if (left == null) return `${top}px ${right}px ${bottom}px`;
  return `${top}px ${right}px ${bottom}px ${left}px`;
}

/** Build a transition shorthand. */
export function transition(property: string, durationMs: number, easing = 'ease'): string {
  return `${property} ${durationMs}ms ${easing}`;
}

/** Combine multiple transition shorthands into a single value. */
export function transitions(...items: string[]): string {
  return items.join(', ');
}

/** Build a CSS scale() transform value. */
export function scale(factor: number): string {
  return `scale(${factor})`;
}

/** Build a CSS translateY() transform value. */
export function translateY(px: number): string {
  return `translateY(${px}px)`;
}

/** Build a combined scale + translateY transform. */
export function scaleTranslateY(s: number, y: number): string {
  return `scale(${s}) translateY(${y}px)`;
}
