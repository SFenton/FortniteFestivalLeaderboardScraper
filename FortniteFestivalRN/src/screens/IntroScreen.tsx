import React, {useEffect, useRef, useState} from 'react';
import {
  Animated,
  ImageBackground,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import {LiquidGlassView, isLiquidGlassSupported} from '@callstack/liquid-glass';

// Log immediately on import
console.log('[LiquidGlass] isLiquidGlassSupported:', isLiquidGlassSupported);
console.log('[LiquidGlass] Platform.OS:', Platform.OS);
console.log('[LiquidGlass] Platform.Version:', Platform.Version);
console.log('[LiquidGlass] Check Xcode console for native debug output!');

// Background image for testing liquid glass effect visibility
const BACKGROUND_IMAGE = {
  uri: 'https://images.unsplash.com/photo-1534796636912-3b95b3ab5986?w=1200&q=80',
};

type Props = {
  onContinue: () => void;
};

/**
 * Intro screen to test liquid glass rendering outside of the navigation stack.
 * Uses transform animations instead of opacity to avoid iOS 26 UIVisualEffectView bugs.
 */
export function IntroScreen({onContinue}: Props) {
  // Test toggling effect prop (Apple's recommended way to animate glass)
  const [glassEffect, setGlassEffect] = useState<'none' | 'regular' | 'clear'>('regular');
  
  // Use scale animation instead of opacity (opacity breaks glass on iOS 26.1)
  const scaleAnim = useRef(new Animated.Value(0.8)).current;
  const translateYAnim = useRef(new Animated.Value(50)).current;

  useEffect(() => {
    // Animate in using transforms (NOT opacity)
    Animated.parallel([
      Animated.spring(scaleAnim, {
        toValue: 1,
        useNativeDriver: true,
        tension: 50,
        friction: 7,
      }),
      Animated.spring(translateYAnim, {
        toValue: 0,
        useNativeDriver: true,
        tension: 50,
        friction: 7,
      }),
    ]).start();
  }, [scaleAnim, translateYAnim]);

  const toggleEffect = () => {
    // Cycle through effects to test Apple's materialize/dematerialize animation
    setGlassEffect(prev => {
      if (prev === 'regular') return 'clear';
      if (prev === 'clear') return 'none';
      return 'regular';
    });
  };

  return (
    <ImageBackground
      source={BACKGROUND_IMAGE}
      style={styles.background}
      resizeMode="cover"
    >
      <View style={styles.container}>
        <Text style={styles.title}>Liquid Glass Test</Text>
        <Text style={styles.subtitle}>
          isLiquidGlassSupported: {isLiquidGlassSupported ? 'YES ✓' : 'NO ✗'}
        </Text>
        <Text style={styles.hint}>
          Using transform animations (no opacity) to avoid iOS 26.1 bugs
        </Text>

        {/* Animated container using scale/translate instead of opacity */}
        <Animated.View
          style={[
            styles.glassContainer,
            {
              transform: [
                {scale: scaleAnim},
                {translateY: translateYAnim},
              ],
            },
          ]}
        >
          {/* Static glass with tintColor to make effect more visible */}
          <LiquidGlassView 
            style={styles.glassBox} 
            effect="regular" 
            colorScheme="light"
            tintColor="rgba(255, 100, 100, 0.3)"
          >
            <Text style={styles.glassText}>Regular</Text>
            <Text style={styles.glassSubtext}>tint: red</Text>
          </LiquidGlassView>

          <LiquidGlassView 
            style={styles.glassBox} 
            effect="clear" 
            colorScheme="dark"
            tintColor="rgba(100, 100, 255, 0.3)"
          >
            <Text style={styles.glassText}>Clear</Text>
            <Text style={styles.glassSubtext}>tint: blue</Text>
          </LiquidGlassView>

          {/* Toggleable glass - test effect prop animation */}
          <Pressable onPress={toggleEffect}>
            <LiquidGlassView
              style={styles.glassBoxLarge}
              effect={glassEffect}
              colorScheme="dark"
              interactive
              tintColor="rgba(100, 255, 100, 0.3)"
            >
              <Text style={styles.glassText}>Tap to toggle effect</Text>
              <Text style={styles.glassSubtext}>Current: {glassEffect}</Text>
            </LiquidGlassView>
          </Pressable>
        </Animated.View>

        {/* Comparison: Regular View with no glass */}
        <View style={styles.fallbackBox}>
          <Text style={styles.glassText}>Regular View (no glass)</Text>
          <Text style={styles.glassSubtext}>For comparison</Text>
        </View>

        <Text style={styles.hint}>
          Check Xcode console for native UIGlassEffect debug output
        </Text>

        <Pressable style={styles.button} onPress={onContinue}>
          <Text style={styles.buttonText}>Continue to App →</Text>
        </Pressable>
      </View>
    </ImageBackground>
  );
}

const styles = StyleSheet.create({
  background: {
    flex: 1,
  },
  container: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    padding: 24,
  },
  title: {
    fontSize: 28,
    fontWeight: '700',
    color: '#FFFFFF',
    marginBottom: 8,
    textShadowColor: 'rgba(0,0,0,0.5)',
    textShadowOffset: {width: 0, height: 1},
    textShadowRadius: 4,
  },
  subtitle: {
    fontSize: 16,
    fontWeight: '600',
    color: '#FFFFFF',
    marginBottom: 8,
    textShadowColor: 'rgba(0,0,0,0.5)',
    textShadowOffset: {width: 0, height: 1},
    textShadowRadius: 4,
  },
  hint: {
    fontSize: 12,
    color: 'rgba(255,255,255,0.8)',
    marginBottom: 24,
    textAlign: 'center',
  },
  glassContainer: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    justifyContent: 'center',
    gap: 16,
    marginBottom: 24,
  },
  glassBox: {
    width: 140,
    height: 100,
    borderRadius: 20,
    alignItems: 'center',
    justifyContent: 'center',
    padding: 12,
  },
  glassBoxLarge: {
    width: 300,
    height: 100,
    borderRadius: 24,
    alignItems: 'center',
    justifyContent: 'center',
    padding: 16,
  },
  glassText: {
    fontSize: 14,
    fontWeight: '600',
    color: '#FFFFFF',
    textAlign: 'center',
  },
  glassSubtext: {
    fontSize: 11,
    color: 'rgba(255,255,255,0.7)',
    marginTop: 4,
  },
  fallbackBox: {
    width: 300,
    height: 80,
    borderRadius: 20,
    backgroundColor: 'rgba(255,255,255,0.15)',
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: 32,
  },
  button: {
    backgroundColor: '#7C3AED',
    paddingHorizontal: 32,
    paddingVertical: 16,
    borderRadius: 12,
  },
  buttonText: {
    fontSize: 16,
    fontWeight: '600',
    color: '#FFFFFF',
  },
});
