/**
 * SVG-based drop-in replacement for react-native-linear-gradient.
 *
 * The native BVLinearGradient Windows module targets UWP XAML
 * (Windows.UI.Xaml) which is incompatible with RN-Windows 0.81's
 * WinUI 3 (Microsoft.UI.Xaml).  This shim provides the same API
 * using react-native-svg, which already has a working Windows build.
 *
 * On iOS / Android the real native module is used instead (Metro
 * only redirects this shim for the "windows" platform).
 */

import React from 'react';
import {View, type ViewStyle, type StyleProp} from 'react-native';
import Svg, {Defs, LinearGradient as SvgLinearGradient, Stop, Rect} from 'react-native-svg';

interface Point {
  x: number;
  y: number;
}

interface LinearGradientProps {
  colors: string[];
  locations?: number[];
  start?: Point;
  end?: Point;
  useAngle?: boolean;
  angle?: number;
  angleCenter?: Point;
  style?: StyleProp<ViewStyle>;
  children?: React.ReactNode;
}

/**
 * Converts a normalised 0-1 point to SVG percentage coordinates.
 */
function pct(v: number): string {
  return `${(v * 100).toFixed(2)}%`;
}

function LinearGradient({
  colors,
  locations,
  start = {x: 0.5, y: 0},
  end = {x: 0.5, y: 1},
  style,
  children,
}: LinearGradientProps) {
  return (
    <View style={[{overflow: 'hidden'}, style]}>
      {/* Absolute-fill SVG that draws the gradient */}
      <Svg style={{position: 'absolute', top: 0, left: 0, right: 0, bottom: 0}}>
        <Defs>
          <SvgLinearGradient
            id="grad"
            x1={pct(start.x)}
            y1={pct(start.y)}
            x2={pct(end.x)}
            y2={pct(end.y)}>
            {colors.map((color, i) => (
              <Stop
                key={i}
                offset={
                  locations && locations[i] != null
                    ? pct(locations[i])
                    : pct(i / Math.max(colors.length - 1, 1))
                }
                stopColor={color}
                stopOpacity={1}
              />
            ))}
          </SvgLinearGradient>
        </Defs>
        <Rect x="0" y="0" width="100%" height="100%" fill="url(#grad)" />
      </Svg>

      {/* Overlay children exactly like the native component */}
      {children}
    </View>
  );
}

export default LinearGradient;
export {LinearGradient};
