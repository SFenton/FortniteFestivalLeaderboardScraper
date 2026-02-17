import React from 'react';
import {StyleSheet, View} from 'react-native';

export type DifficultyBarsProps = {
  rawDifficulty: number;
  compact?: boolean;
  barWidth?: number;
  barHeight?: number;
  gap?: number;
};

/**
 * View-based difficulty bars using plain rectangles with a skew transform.
 * Used on Windows where react-native-svg native modules aren't available.
 */
export function DifficultyBars(props: DifficultyBarsProps) {
  const raw = Number.isFinite(props.rawDifficulty) ? props.rawDifficulty : 0;
  const display = Math.max(0, Math.min(6, Math.trunc(raw))) + 1;
  const barW = props.barWidth ?? (props.compact ? 20 : 16);
  const barH = props.barHeight ?? (props.compact ? 40 : 34);
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
          <View
            key={idx}
            style={{
              width: barW,
              height: barH,
              backgroundColor: filled ? '#FFFFFF' : '#666666',
              borderRadius: 2,
              transform: [{skewX: '-8deg'}],
            }}
          />
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
