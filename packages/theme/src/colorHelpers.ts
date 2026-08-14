import { Colors } from './colors';

export function accuracyColor(percent: number): string {
  const position = Math.min(Math.max(percent / 100, 0), 1);
  const red = Math.round(
    Colors.accuracyLow.r * (1 - position) + Colors.accuracyHigh.r * position,
  );
  const green = Math.round(
    Colors.accuracyLow.g * (1 - position) + Colors.accuracyHigh.g * position,
  );
  const blue = Math.round(
    Colors.accuracyLow.b * (1 - position) + Colors.accuracyHigh.b * position,
  );
  return `rgb(${red},${green},${blue})`;
}

export const ACCURACY_GRADIENT =
  `linear-gradient(to right, ${accuracyColor(0)}, ${accuracyColor(100)})`;
