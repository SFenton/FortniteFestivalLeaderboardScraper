import React, {useCallback, useMemo, useState} from 'react';
import {StyleSheet, View} from 'react-native';

type Props = {
  min: number;
  max: number;
  value: number;
  onChange: (next: number) => void;
  disabled?: boolean;
};

const clamp = (v: number, min: number, max: number) => Math.max(min, Math.min(max, v));

export function IntSlider(props: Props) {
  const {min, max, value, onChange, disabled} = props;

  const [width, setWidth] = useState(0);

  const safeMin = Math.min(min, max);
  const safeMax = Math.max(min, max);
  const range = Math.max(1, safeMax - safeMin);

  const clampedValue = useMemo(() => clamp(Math.round(value), safeMin, safeMax), [safeMax, safeMin, value]);
  const pct = useMemo(() => (clampedValue - safeMin) / range, [clampedValue, range, safeMin]);

  const setFromX = useCallback(
    (x: number) => {
      if (disabled) return;
      if (width <= 0) return;

      const xClamped = clamp(x, 0, width);
      const raw = safeMin + (xClamped / width) * range;
      const next = clamp(Math.round(raw), safeMin, safeMax);
      if (next !== clampedValue) onChange(next);
    },
    [clampedValue, disabled, onChange, range, safeMax, safeMin, width],
  );

  const thumbLeft = useMemo(() => {
    if (width <= 0) return 0;
    const usable = Math.max(1, width - THUMB_SIZE);
    return clamp(pct * usable, 0, usable);
  }, [pct, width]);

  return (
    <View
      style={[styles.wrap, disabled && styles.wrapDisabled]}
      onLayout={e => setWidth(Math.max(0, Math.floor(e.nativeEvent.layout.width)))}
      onStartShouldSetResponder={() => !disabled}
      onMoveShouldSetResponder={() => !disabled}
      onResponderGrant={e => setFromX(e.nativeEvent.locationX)}
      onResponderMove={e => setFromX(e.nativeEvent.locationX)}
      accessibilityRole="adjustable"
      accessibilityValue={{min: safeMin, max: safeMax, now: clampedValue}}
    >
      <View style={styles.track}>
        <View style={[styles.fill, {width: `${Math.max(0, Math.min(1, pct)) * 100}%`}]} />
      </View>
      <View style={[styles.thumb, {left: thumbLeft}]} />
    </View>
  );
}

const THUMB_SIZE = 20;

const styles = StyleSheet.create({
  wrap: {
    height: 32,
    justifyContent: 'center',
  },
  wrapDisabled: {
    opacity: 0.5,
  },
  track: {
    height: 6,
    borderRadius: 999,
    backgroundColor: '#0B1220',
    borderWidth: 1,
    borderColor: '#2B3B55',
    overflow: 'hidden',
  },
  fill: {
    height: '100%',
    backgroundColor: '#4C7DFF',
  },
  thumb: {
    position: 'absolute',
    width: THUMB_SIZE,
    height: THUMB_SIZE,
    borderRadius: THUMB_SIZE / 2,
    backgroundColor: '#E9F0FF',
    borderWidth: 2,
    borderColor: '#4C7DFF',
  },
});
