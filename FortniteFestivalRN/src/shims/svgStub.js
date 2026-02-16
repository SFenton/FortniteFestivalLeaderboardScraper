/**
 * svgStub – Windows fallback for react-native-svg.
 *
 * react-native-svg's native modules (RNSVGGroup, RNSVGSvgView, etc.)
 * are not available on the RN-Windows WinUI 3 Composition renderer.
 * This stub provides no-op components so transitive imports don't crash.
 *
 * Components that need real SVG rendering on Windows should use
 * platform-specific .windows.tsx overrides with View-based alternatives.
 */
import React from 'react';
import { View } from 'react-native';

// Svg container — renders as a plain View
const Svg = React.forwardRef(({ children, width, height, style, ...rest }, ref) => (
  <View ref={ref} style={[{ width, height }, style]} {...rest}>{children}</View>
));
Svg.displayName = 'SvgStub';

// Shape primitives — render nothing (they need native SVG to draw)
const noop = React.forwardRef((props, ref) => null);
noop.displayName = 'SvgNoopStub';

export const Circle = noop;
export const Ellipse = noop;
export const G = noop;
export const Line = noop;
export const Path = noop;
export const Polygon = noop;
export const Polyline = noop;
export const Rect = noop;
export const Text = noop;
export const TSpan = noop;
export const TextPath = noop;
export const Use = noop;
export const Image = noop;
export const ClipPath = noop;
export const Defs = noop;
export const LinearGradient = noop;
export const RadialGradient = noop;
export const Stop = noop;
export const Mask = noop;
export const Pattern = noop;
export const Symbol = noop;
export const ForeignObject = noop;
export const Marker = noop;

export { Svg };
export default Svg;
