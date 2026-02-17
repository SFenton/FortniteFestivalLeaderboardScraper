import React from 'react';
import {StyleSheet, View} from 'react-native';
import Svg, {Polygon} from 'react-native-svg';

export type DifficultyBarsProps = {
  rawDifficulty: number;
  compact?: boolean;
  barWidth?: number;
  barHeight?: number;
  gap?: number;
};

/**
 * SVG-based difficulty bars rendered as parallelogram polygons.
 * Used on iOS / Android where react-native-svg is available.
 */
export function DifficultyBars(props: DifficultyBarsProps) {
  const raw = Number.isFinite(props.rawDifficulty) ? props.rawDifficulty : 0;
  const display = Math.max(0, Math.min(6, Math.trunc(raw))) + 1;
  const barW = props.barWidth ?? (props.compact ? 20 : 16);
  const barH = props.barHeight ?? (props.compact ? 40 : 34);
  const offset = Math.min(Math.max(Math.round(barW * 0.26), 1), Math.floor(barW * 0.45));
  const gap = props.gap ?? (props.compact ? 2 : 1);

  return (
    <View
      style={[styles.diffRow, {gap}]}
      accessibilityRole="text"
      accessibilityLabel={`Difficulty ${display} of 7`}
    >
      {Array.from({length: 7}).map((_, idx) => {
        const filled = idx + 1 <= display;
        return (
          <View key={idx} style={{width: barW, height: barH}}>
            <Svg width={barW} height={barH}>
              <Polygon
                points={`${offset},0 ${barW},0 ${barW - offset},${barH} 0,${barH}`}
                fill={filled ? '#FFFFFF' : '#666666'}
              />
            </Svg>
          </View>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  diffRow: {
    flexDirection: 'row',
    alignItems: 'center',
  },
});
