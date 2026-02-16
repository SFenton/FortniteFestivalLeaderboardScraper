import React, {useCallback, useEffect, useMemo, useRef} from 'react';
import {Animated, Dimensions, Easing, Image, Platform, StyleSheet, View} from 'react-native';
import {useWindowSize} from './useWindowSize';
import type {Song} from '@festival/core';

// ── Configuration ───────────────────────────────────────────────────
const TILE_SIZE = 90; // Width & height of each album art tile
const TILE_GAP = 6; // Gap between tiles
const TILE_RADIUS = 10; // Border radius for each tile
const CYCLE_DURATION = 25_000; // ms for one full scroll cycle
const MIN_IMAGES = 100; // Minimum cached images before activating
const DIM_OPACITY = 0.72; // Overlay darkness to keep foreground legible

const STEP_SIZE = TILE_SIZE + TILE_GAP;

// RNW native-driver transforms are unreliable (same as App.tsx);
// keep animations JS-driven on Windows.
const USE_NATIVE_DRIVER = Platform.OS !== 'windows';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Compute tile/row counts from current window dimensions. */
function computeCounts(screenWidth: number, screenHeight: number) {
  const visibleCount = Math.ceil(screenWidth / STEP_SIZE) + 5;
  const halfCount = visibleCount + 2;
  const rowCount = Math.ceil(screenHeight / STEP_SIZE) + 1;
  return {halfCount, rowCount};
}

/** Build a shuffled strip of `count` images, mirrored into two halves. */
function buildMirroredStrip(candidates: string[], halfCount: number): string[] {
  const half: string[] = [];
  const shuffled = [...candidates].sort(() => Math.random() - 0.5);
  for (let i = 0; i < halfCount; i++) {
    half.push(shuffled[i % shuffled.length]);
  }
  return [...half, ...half]; // second half = mirror → seamless loop reset
}

// ---------------------------------------------------------------------------
// Single animated row — continuous native-driver loop with periodic tile
// recycling that happens invisibly at the loop-reset boundary.
// ---------------------------------------------------------------------------

function SlidingRow({
  initialStrip,
  direction,
  rowIndex,
  halfCount,
}: {
  initialStrip: string[];
  direction: 'left' | 'right';
  rowIndex: number;
  halfCount: number;
}) {
  const anim = useRef(new Animated.Value(0)).current;

  const halfWidth = halfCount * STEP_SIZE;

  // Continuous loop — runs entirely on the native thread, no JS round-trips.
  useEffect(() => {
    const loop = Animated.loop(
      Animated.timing(anim, {
        toValue: 1,
        duration: CYCLE_DURATION,
        easing: Easing.linear,
        useNativeDriver: USE_NATIVE_DRIVER,
      }),
    );
    loop.start();
    return () => loop.stop();
  }, [anim]);

  // Left:  0 → -halfWidth   (tiles scroll left)
  // Right: -halfWidth → 0   (tiles scroll right)
  const translateX = anim.interpolate({
    inputRange: [0, 1],
    outputRange:
      direction === 'left' ? [0, -halfWidth] : [-halfWidth, 0],
  });

  return (
    <Animated.View
      style={[
        styles.row,
        {
          top: rowIndex * STEP_SIZE,
          transform: [{translateX}],
        },
      ]}
    >
      {initialStrip.map((uri, idx) => (
        <View key={idx} style={styles.tileWrap}>
          <Image
            source={{uri}}
            style={styles.tile}
            resizeMode="cover"
          />
        </View>
      ))}
    </Animated.View>
  );
}

// ---------------------------------------------------------------------------
// Public component
// ---------------------------------------------------------------------------

const FADE_DURATION = 1_000; // ms to fade rows in once ready

export function SlidingRowsBackground(props: {songs: Song[]}) {
  const {songs} = props;

  const {width: screenWidth, height: screenHeight} = useWindowSize();
  const {halfCount, rowCount} = useMemo(
    () => computeCounts(screenWidth, screenHeight),
    [screenWidth, screenHeight],
  );

  const fadeAnim = useRef(new Animated.Value(0)).current;
  const hasFadedIn = useRef(false);

  const imageCandidates = useMemo(
    () => {
      const raw = songs.map(s => s.imagePath).filter(Boolean) as string[];
      // Ensure file:// prefix for local paths on iOS
      return raw.map(p => {
        if (p.startsWith('/') && !p.startsWith('file://')) {
          return Platform.OS === 'ios' ? `file://${p}` : p;
        }
        return p;
      });
    },
    [songs],
  );

  const active = imageCandidates.length >= MIN_IMAGES;

  const rowStrips = useMemo(() => {
    if (!active) return [];
    return Array.from({length: rowCount}, () =>
      buildMirroredStrip(imageCandidates, halfCount),
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, imageCandidates, rowCount, halfCount]);

  // Once active (all rows populated), begin the fade-in.
  // The rows are already scrolling at opacity 0; this reveals them.
  useEffect(() => {
    if (active && rowStrips.length > 0 && !hasFadedIn.current) {
      hasFadedIn.current = true;
      Animated.timing(fadeAnim, {
        toValue: 1,
        duration: FADE_DURATION,
        easing: Easing.out(Easing.ease),
        useNativeDriver: USE_NATIVE_DRIVER,
      }).start();
    }
  }, [active, rowStrips.length, fadeAnim]);

  if (!active) return null;

  return (
    <View style={styles.container} pointerEvents="none">
      <Animated.View style={[styles.rowsContainer, {opacity: fadeAnim}]}>
        {rowStrips.map((strip, i) => (
          <SlidingRow
            key={i}
            initialStrip={strip}
            direction={i % 2 === 0 ? 'left' : 'right'}
            rowIndex={i}
            halfCount={halfCount}
          />
        ))}
      </Animated.View>

      {/* dark overlay so intro content stays readable */}
      <View style={[styles.dim, {opacity: DIM_OPACITY}]} />
    </View>
  );
}

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const styles = StyleSheet.create({
  container: {
    ...StyleSheet.absoluteFillObject,
    overflow: 'hidden',
    zIndex: 0,
    elevation: 0,
  },
  rowsContainer: {
    ...StyleSheet.absoluteFillObject,
  },
  row: {
    position: 'absolute',
    flexDirection: 'row',
    left: 0,
  },
  tileWrap: {
    width: TILE_SIZE,
    height: TILE_SIZE,
    marginRight: TILE_GAP,
    borderRadius: TILE_RADIUS,
    overflow: 'hidden',
    backgroundColor: 'rgba(122, 43, 149, 0.3)', // subtle purple placeholder
  },
  tile: {
    width: '100%',
    height: '100%',
  },
  dim: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: '#1A0A2E', // match IntroScreen background purple
  },
});
