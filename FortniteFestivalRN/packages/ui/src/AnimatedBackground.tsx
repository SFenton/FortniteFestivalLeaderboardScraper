import React, {useCallback, useEffect, useRef, useState} from 'react';
import {Animated, StyleSheet, View} from 'react-native';
import type {Song} from '@festival/core';

const FADE_DURATION = 1000; // 1 second fade
const DISPLAY_DURATION = 5000; // 5 seconds display time
// Total time a single motion animation runs (visible duration + crossfade).
const MOTION_DURATION = DISPLAY_DURATION + FADE_DURATION;

// ---------------------------------------------------------------------------
// Motion presets – each defines a start→end value for scale / translateX /
// translateY.  Pan presets use a slight scale-up so edges are never exposed.
// ---------------------------------------------------------------------------

type MotionPreset = {
  scale: [number, number];
  translateX: [number, number];
  translateY: [number, number];
};

const MOTION_PRESETS: MotionPreset[] = [
  // Zoom in
  {scale: [1.0, 1.12], translateX: [0, 0], translateY: [0, 0]},
  // Zoom out
  {scale: [1.12, 1.0], translateX: [0, 0], translateY: [0, 0]},
  // Pan left (starts further right)
  {scale: [1.18, 1.18], translateX: [18, -18], translateY: [0, 0]},
  // Pan right (starts further left)
  {scale: [1.18, 1.18], translateX: [-18, 18], translateY: [0, 0]},
  // Pan up (starts further down)
  {scale: [1.18, 1.18], translateX: [0, 0], translateY: [18, -18]},
  // Pan down (starts further up)
  {scale: [1.18, 1.18], translateX: [0, 0], translateY: [-18, 18]},
  // Diagonal ↘  (starts top-left)
  {scale: [1.18, 1.18], translateX: [-14, 14], translateY: [-14, 14]},
  // Diagonal ↙  (starts top-right)
  {scale: [1.18, 1.18], translateX: [14, -14], translateY: [-14, 14]},
  // Diagonal ↗  (starts bottom-left)
  {scale: [1.18, 1.18], translateX: [-14, 14], translateY: [14, -14]},
  // Diagonal ↖  (starts bottom-right)
  {scale: [1.18, 1.18], translateX: [14, -14], translateY: [14, -14]},
];

function randomMotion(): MotionPreset {
  return MOTION_PRESETS[Math.floor(Math.random() * MOTION_PRESETS.length)];
}

// ---------------------------------------------------------------------------
// Layer – each of the two independent image layers owns its own Animated
// values.  Transform properties are animated directly (no interpolation
// objects), so setValue() immediately places them at the correct position
// on the native thread with zero frame-lag.
// ---------------------------------------------------------------------------

interface Layer {
  opacity: Animated.Value;
  scale: Animated.Value;
  translateX: Animated.Value;
  translateY: Animated.Value;
  imageIndex: number;
}

function makeLayer(imageIndex: number): Layer {
  return {
    opacity: new Animated.Value(0),
    scale: new Animated.Value(1),
    translateX: new Animated.Value(0),
    translateY: new Animated.Value(0),
    imageIndex,
  };
}

/** Start (or restart) the motion animation on a layer. */
function startMotion(layer: Layer) {
  const preset = randomMotion();

  // Snap to start position – setValue() pushes straight to native.
  layer.scale.setValue(preset.scale[0]);
  layer.translateX.setValue(preset.translateX[0]);
  layer.translateY.setValue(preset.translateY[0]);

  // Animate to end position.
  Animated.parallel([
    Animated.timing(layer.scale, {
      toValue: preset.scale[1],
      duration: MOTION_DURATION,
      useNativeDriver: true,
    }),
    Animated.timing(layer.translateX, {
      toValue: preset.translateX[1],
      duration: MOTION_DURATION,
      useNativeDriver: true,
    }),
    Animated.timing(layer.translateY, {
      toValue: preset.translateY[1],
      duration: MOTION_DURATION,
      useNativeDriver: true,
    }),
  ]).start();
}

export function AnimatedBackground(props: {songs: Song[]; animate?: boolean; dimOpacity?: number}) {
  const {songs} = props;

  const animate = props.animate ?? true;
  const dimOpacity = props.dimOpacity ?? 0.7;

  const [imageUris, setImageUris] = useState<string[]>([]);

  // Two persistent layers – never recreated.
  const layerA = useRef<Layer>(makeLayer(0)).current;
  const layerB = useRef<Layer>(makeLayer(1)).current;

  // Which layer is currently the visible ("active") one.
  const activeRef = useRef<'A' | 'B'>('A');

  // Running image counter – tracks which imageUris index to assign next.
  const imgCursor = useRef(2); // 0 & 1 already assigned to A & B

  // Force a render when layer image indices change (since they live in refs).
  const [, forceRender] = useState(0);

  // Get songs with images (as a stable list for effect dependencies).
  const imageCandidates = React.useMemo(
    () => songs.map(s => s.imagePath).filter(Boolean) as string[],
    [songs],
  );

  // Build the shuffled URI list.
  useEffect(() => {
    if (imageCandidates.length === 0) {
      setImageUris([]);
      return;
    }

    const shuffled = [...imageCandidates]
      .sort(() => Math.random() - 0.5)
      .slice(0, Math.min(100, imageCandidates.length));

    if (shuffled.length > 0) {
      setImageUris(shuffled);

      // Reset both layers for the new image set.
      layerA.imageIndex = 0;
      layerA.opacity.setValue(1);
      layerB.imageIndex = shuffled.length > 1 ? 1 : 0;
      layerB.opacity.setValue(0);
      activeRef.current = 'A';
      imgCursor.current = 2;
      forceRender(n => n + 1);
    } else {
      setImageUris([]);
    }
  }, [imageCandidates, layerA, layerB]);

  // Start motion on the initial active layer.
  useEffect(() => {
    if (!animate || imageUris.length === 0) return;
    startMotion(layerA);
  }, [animate, imageUris, layerA]);

  // Transition callback — ping-pongs between layers.
  const doTransition = useCallback(() => {
    const active = activeRef.current === 'A' ? layerA : layerB;
    const standby = activeRef.current === 'A' ? layerB : layerA;

    // Start the standby layer's motion NOW, before it fades in.
    // setValue() snaps the native values to the preset's start position
    // instantly — no interpolation mismatch, no frame-lag.
    startMotion(standby);

    // Cross-fade: active → 0, standby → 1
    Animated.parallel([
      Animated.timing(active.opacity, {
        toValue: 0,
        duration: FADE_DURATION,
        useNativeDriver: true,
      }),
      Animated.timing(standby.opacity, {
        toValue: 1,
        duration: FADE_DURATION,
        useNativeDriver: true,
      }),
    ]).start(() => {
      // Standby is now the visible layer. Flip the pointer.
      activeRef.current = activeRef.current === 'A' ? 'B' : 'A';

      // Load the next image onto the now-hidden (old active) layer so it's
      // ready for the next transition.
      const nextIdx = imgCursor.current % imageUris.length;
      imgCursor.current = nextIdx + 1;
      active.imageIndex = nextIdx;
      forceRender(n => n + 1);
    });
  }, [imageUris, layerA, layerB]);

  // Timer that fires each transition.
  useEffect(() => {
    if (!animate || imageUris.length < 2) return;

    const timer = setInterval(doTransition, DISPLAY_DURATION);
    return () => clearInterval(timer);
  }, [animate, imageUris.length, doTransition]);

  // Don't render if no images.
  if (imageUris.length === 0) return null;

  const uriA = imageUris[layerA.imageIndex];
  const uriB = imageUris[layerB.imageIndex];

  if (!uriA) return null;

  return (
    <View style={styles.container} pointerEvents="none">
      {/* Layer A */}
      <Animated.Image
        source={{uri: uriA}}
        style={[
          styles.bgImage,
          {opacity: animate ? layerA.opacity : 1},
          {
            transform: [
              {scale: layerA.scale},
              {translateX: layerA.translateX},
              {translateY: layerA.translateY},
            ],
          },
        ]}
        resizeMode="cover"
        blurRadius={0}
      />

      {/* Layer B */}
      {animate && uriB && imageUris.length > 1 && (
        <Animated.Image
          source={{uri: uriB}}
          style={[
            styles.bgImage,
            {opacity: layerB.opacity},
            {
              transform: [
                {scale: layerB.scale},
                {translateX: layerB.translateX},
                {translateY: layerB.translateY},
              ],
            },
          ]}
          resizeMode="cover"
          blurRadius={0}
        />
      )}

      {/* Dark overlay for dimming */}
      <View style={[styles.bgDim, {opacity: dimOpacity}]} />
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    ...StyleSheet.absoluteFillObject,
    overflow: 'hidden',
    zIndex: 0,
    elevation: 0,
  },
  bgImage: {
    ...StyleSheet.absoluteFillObject,
  },
  bgDim: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: '#000000',
  },
});
