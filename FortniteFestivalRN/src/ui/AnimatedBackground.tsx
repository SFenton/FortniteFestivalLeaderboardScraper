import React, {useEffect, useRef, useState} from 'react';
import {Animated, StyleSheet, View} from 'react-native';
import {useFestival} from '../app/festival/FestivalContext';

const FADE_DURATION = 1000; // 1 second fade
const DISPLAY_DURATION = 5000; // 10 seconds display time

export function AnimatedBackground(props: {animate?: boolean; dimOpacity?: number}) {
  const {
    state: {songs},
  } = useFestival();

  const animate = props.animate ?? true;
  const dimOpacity = props.dimOpacity ?? 0.7;

  const [currentIndex, setCurrentIndex] = useState(0);
  const [nextIndex, setNextIndex] = useState(1);
  const [imageUris, setImageUris] = useState<string[]>([]);
  const currentOpacity = useRef(new Animated.Value(1)).current;
  const nextOpacity = useRef(new Animated.Value(0)).current;

  // Get songs with images (as a stable list for effect dependencies).
  const imageCandidates = React.useMemo(
    () => songs.map(s => s.imagePath).filter(Boolean) as string[],
    [songs],
  );
  
  useEffect(() => {
    if (imageCandidates.length === 0) {
      setImageUris([]);
      return;
    }

    // Create a shuffled list of image URIs
    const shuffled = [...imageCandidates]
      .sort(() => Math.random() - 0.5)
      .slice(0, Math.min(100, imageCandidates.length)); // Limit to 100 to avoid memory issues

    if (shuffled.length > 0) {
      setImageUris(shuffled);
      setCurrentIndex(0);
      setNextIndex(shuffled.length > 1 ? 1 : 0);

      // Reset opacities whenever we (re)seed the image list.
      currentOpacity.setValue(1);
      nextOpacity.setValue(0);
    } else {
      setImageUris([]);
    }
  }, [currentOpacity, imageCandidates, nextOpacity]);

  useEffect(() => {
    if (!animate) {
      // Static mode: keep the first image and don't cross-fade.
      currentOpacity.setValue(1);
      nextOpacity.setValue(0);
      return;
    }

    if (imageUris.length < 2) return; // Need at least 2 images to animate

    const timer = setInterval(() => {
      // Start fade animation
      Animated.parallel([
        Animated.timing(currentOpacity, {
          toValue: 0,
          duration: FADE_DURATION,
          useNativeDriver: true,
        }),
        Animated.timing(nextOpacity, {
          toValue: 1,
          duration: FADE_DURATION,
          useNativeDriver: true,
        }),
      ]).start(() => {
        // After fade completes, swap the indices
        const newCurrent = nextIndex;
        const newNext = (nextIndex + 1) % imageUris.length;
        
        setCurrentIndex(newCurrent);
        setNextIndex(newNext);
        
        // Reset opacities for next transition
        currentOpacity.setValue(1);
        nextOpacity.setValue(0);
      });
    }, DISPLAY_DURATION);

    return () => clearInterval(timer);
  }, [animate, imageUris.length, currentOpacity, nextIndex, nextOpacity]);

  // Don't render if no images
  if (imageUris.length === 0) {
    return null;
  }

  const currentUri = imageUris[currentIndex];
  const nextUri = imageUris[nextIndex];

  if (!currentUri) {
    return null;
  }

  return (
    <View style={styles.container} pointerEvents="none">
      {/* Current image */}
      <Animated.Image
        source={{uri: currentUri}}
        style={[styles.bgImage, {opacity: animate ? currentOpacity : 1}]}
        resizeMode="cover"
        blurRadius={0}
      />
      
      {/* Next image (fading in) */}
      {animate && nextUri && imageUris.length > 1 && (
        <Animated.Image
          source={{uri: nextUri}}
          style={[styles.bgImage, {opacity: nextOpacity}]}
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
    // Render order already places this behind NavigationContainer.
    // Negative zIndex can end up behind the parent background on Android.
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
