import React, {useCallback, useEffect, useRef, useState} from 'react';
import {
  ActivityIndicator,
  Animated,
  Dimensions,
  Easing,
  Platform,
  StatusBar,
  StyleSheet,
  View,
} from 'react-native';
import {SafeAreaProvider, initialWindowMetrics} from 'react-native-safe-area-context';

import {AppNavigator} from './src/navigation/AppNavigator';
import {FestivalProvider, useFestival} from './src/app/festival/FestivalContext';
import {IntroScreen} from './src/screens/IntroScreen';
import {SlidingRowsBackground} from './src/ui/SlidingRowsBackground';

if (Platform.OS !== 'windows') {
  // `react-native-screens`' Windows native project currently targets UWP/WinUI2,
  // which is incompatible with RNW's default WinUI3 app template.
  // It is optional for React Navigation, so we enable it only on non-Windows.
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  require('react-native-screens').enableScreens();
}

const RootView =
  Platform.OS === 'windows'
    ? View
    : // eslint-disable-next-line @typescript-eslint/no-var-requires
      require('react-native-gesture-handler').GestureHandlerRootView;

const SCREEN_WIDTH = Dimensions.get('window').width;

const ONBOARDING_COMPLETE_KEY = 'fnfestival:onboardingComplete';

// ── Transition phases ───────────────────────────────────────────────
//   loading      → checking AsyncStorage for onboarding flag
//   introSpinner → spinner fades in (0.5 s), carousel mounts invisibly
//   introReveal  → carousel ready; spinner fades out + carousel fades in (0.5 s)
//   intro        → carousel is interactive
//   fadingOut    → carousel fading to nothing (0.5 s)
//   spinner      → white arc spinner fades in (0.5 s), navigator mounts off-screen
//   slidingIn    → navigator slides in from the right
//   done         → navigator is fully visible, intro layers unmounted
type TransitionPhase =
  | 'loading'
  | 'introSpinner'
  | 'introReveal'
  | 'intro'
  | 'fadingOut'
  | 'spinner'
  | 'slidingIn'
  | 'done';

/**
 * Orchestrates the intro → spinner → navigator transition.
 * Must live inside <FestivalProvider> so it can read `isReady`.
 */
function TransitionManager() {
  const {state} = useFestival();
  const {isReady} = state;

  const [phase, setPhase] = useState<TransitionPhase>('loading');
  const [introSpinnerReady, setIntroSpinnerReady] = useState(false);
  const [carouselReady, setCarouselReady] = useState(false);
  const [spinnerFullyVisible, setSpinnerFullyVisible] = useState(false);
  /** True when onboarding was skipped (returning user) — drives the fast-path spinner. */
  const skippedOnboarding = useRef(false);

  // ── Check onboarding flag on mount ────────────────────────────────
  useEffect(() => {
    if (phase !== 'loading') return;
    (async () => {
      try {
        if (process.env.JEST_WORKER_ID) {
          setPhase('introSpinner');
          return;
        }
        // eslint-disable-next-line @typescript-eslint/no-var-requires
        const AsyncStorageModule = require('@react-native-async-storage/async-storage') as {
          default?: unknown;
          getItem?: (k: string) => Promise<string | null>;
        };
        const AsyncStorage = ((AsyncStorageModule as any).default ?? AsyncStorageModule) as {
          getItem: (k: string) => Promise<string | null>;
        };
        const value = await AsyncStorage.getItem(ONBOARDING_COMPLETE_KEY);
        if (value === 'true') {
          // Returning user — skip carousel, go straight to spinner
          skippedOnboarding.current = true;
          setPhase('spinner');
        } else {
          setPhase('introSpinner');
        }
      } catch {
        setPhase('introSpinner');
      }
    })();
  }, [phase]);

  // Animated values (stable refs — safe in dep arrays)
  const introOpacity = useRef(new Animated.Value(0)).current;   // carousel starts hidden
  const introSpinnerOpacity = useRef(new Animated.Value(0)).current;
  const spinnerOpacity = useRef(new Animated.Value(0)).current;
  const navTranslateX = useRef(new Animated.Value(SCREEN_WIDTH)).current;

  // ── Intro entrance: fade in the intro spinner ─────────────────────
  useEffect(() => {
    if (phase !== 'introSpinner') return;
    Animated.timing(introSpinnerOpacity, {
      toValue: 1,
      duration: 500,
      useNativeDriver: true,
    }).start(() => {
      setIntroSpinnerReady(true);
    });
  }, [phase, introSpinnerOpacity]);

  // ── Carousel mounted & ready → reveal it ──────────────────────────
  useEffect(() => {
    if (phase !== 'introSpinner' || !introSpinnerReady || !carouselReady) return;
    setPhase('introReveal');

    Animated.parallel([
      // Fade out intro spinner
      Animated.timing(introSpinnerOpacity, {
        toValue: 0,
        duration: 500,
        useNativeDriver: true,
      }),
      // Fade in carousel
      Animated.timing(introOpacity, {
        toValue: 1,
        duration: 500,
        useNativeDriver: true,
      }),
    ]).start(() => {
      setPhase('intro');
    });
  }, [phase, introSpinnerReady, carouselReady, introSpinnerOpacity, introOpacity]);

  // ── User taps Start / Skip ────────────────────────────────────────
  const handleContinue = useCallback(() => {
    if (phase !== 'intro') {
      return;
    }
    setPhase('fadingOut');

    // Persist that onboarding is complete so we skip next time
    (async () => {
      try {
        if (process.env.JEST_WORKER_ID) return;
        // eslint-disable-next-line @typescript-eslint/no-var-requires
        const AsyncStorageModule = require('@react-native-async-storage/async-storage') as {
          default?: unknown;
          setItem?: (k: string, v: string) => Promise<void>;
        };
        const AsyncStorage = ((AsyncStorageModule as any).default ?? AsyncStorageModule) as {
          setItem: (k: string, v: string) => Promise<void>;
        };
        await AsyncStorage.setItem(ONBOARDING_COMPLETE_KEY, 'true');
      } catch {
        // ignore
      }
    })();

    // 1️⃣  Fade carousel to nothing over 0.5 s
    Animated.timing(introOpacity, {
      toValue: 0,
      duration: 500,
      useNativeDriver: true,
    }).start(() => {
      setPhase('spinner');

      // 2️⃣  Fade in spinner over 0.5 s
      Animated.timing(spinnerOpacity, {
        toValue: 1,
        duration: 500,
        useNativeDriver: true,
      }).start(() => {
        setSpinnerFullyVisible(true);
      });
    });
  }, [phase, introOpacity, spinnerOpacity]);

  // ── Outro: spinner stays up until sync finishes, then mount + slide nav ──
  const [navMounted, setNavMounted] = useState(false);

  // Wait for spinner to be fully visible AND isReady before mounting navigator
  useEffect(() => {
    if (phase !== 'spinner' || !spinnerFullyVisible || !isReady) {
      return;
    }
    // Sync is done — mount the navigator off-screen so it can render
    setNavMounted(true);
  }, [phase, spinnerFullyVisible, isReady]);

  // Once navigator is mounted, give it a moment to paint, then slide in
  useEffect(() => {
    if (phase !== 'spinner' || !navMounted) {
      return;
    }

    const timer = setTimeout(() => {
      setPhase('slidingIn');

      Animated.parallel([
        // Fade spinner out
        Animated.timing(spinnerOpacity, {
          toValue: 0,
          duration: 400,
          useNativeDriver: true,
        }),
        // Slide navigator in from the right
        Animated.timing(navTranslateX, {
          toValue: 0,
          duration: 500,
          easing: Easing.out(Easing.cubic),
          useNativeDriver: true,
        }),
      ]).start(() => {
        setPhase('done');
      });
    }, 300);

    return () => clearTimeout(timer);
  }, [phase, navMounted, spinnerOpacity, navTranslateX]);

  // ── Returning-user path: spinner is already visible, just wait for isReady ──
  useEffect(() => {
    if (phase !== 'spinner' || !skippedOnboarding.current || spinnerFullyVisible) return;
    // If we jumped straight to 'spinner' (returning user), fade spinner in
    Animated.timing(spinnerOpacity, {
      toValue: 1,
      duration: 500,
      useNativeDriver: true,
    }).start(() => {
      setSpinnerFullyVisible(true);
    });
  }, [phase, spinnerFullyVisible, spinnerOpacity]);

  // ── Derived visibility flags ──────────────────────────────────────
  const mountCarousel =
    phase === 'introSpinner' ||
    phase === 'introReveal' ||
    phase === 'intro' ||
    phase === 'fadingOut';
  const carouselRevealed = phase === 'intro' || phase === 'fadingOut';
  const showIntroSpinner = phase === 'introSpinner' || phase === 'introReveal';
  const showOutroSpinner = phase === 'loading' || phase === 'spinner' || phase === 'slidingIn';
  const shouldMountNav = navMounted || phase === 'slidingIn' || phase === 'done';

  return (
    <View style={transitionStyles.root}>
      {/* Animated album art mosaic — lives behind everything and persists
          through carousel → spinner → slide-in so it never flickers. */}
      {phase !== 'done' && <SlidingRowsBackground />}

      {/* Carousel — mounted during introSpinner (invisible) so it can warm up */}
      {mountCarousel && (
        <Animated.View style={[StyleSheet.absoluteFill, {opacity: introOpacity}]}>
          <IntroScreen
            onContinue={handleContinue}
            onReady={() => setCarouselReady(true)}
            revealed={carouselRevealed}
          />
        </Animated.View>
      )}

      {/* Intro spinner — shown while carousel is loading */}
      {showIntroSpinner && (
        <Animated.View
          style={[
            StyleSheet.absoluteFill,
            transitionStyles.spinnerContainer,
            {opacity: introSpinnerOpacity},
          ]}>
          <ActivityIndicator size="large" color="#FFFFFF" />
        </Animated.View>
      )}

      {/* Outro spinner — shown after carousel dismissed, while navigator preloads */}
      {showOutroSpinner && (
        <Animated.View
          style={[
            StyleSheet.absoluteFill,
            transitionStyles.spinnerContainer,
            {opacity: spinnerOpacity},
          ]}>
          <ActivityIndicator size="large" color="#FFFFFF" />
        </Animated.View>
      )}

      {/* Main navigator — only mounted once sync is complete */}
      {shouldMountNav && (
        <Animated.View
          style={[
            StyleSheet.absoluteFill,
            {transform: [{translateX: navTranslateX}]},
          ]}>
          <AppNavigator />
        </Animated.View>
      )}
    </View>
  );
}

const transitionStyles = StyleSheet.create({
  root: {
    flex: 1,
    backgroundColor: '#1A0A2E', // matches intro background
  },
  spinnerContainer: {
    justifyContent: 'center',
    alignItems: 'center',
  },
});

// ── Root component ──────────────────────────────────────────────────

function App() {
  console.log('[App] Rendering App component, Platform:', Platform.OS);

  return (
    <SafeAreaProvider initialMetrics={initialWindowMetrics}>
      <StatusBar barStyle="light-content" />
      <RootView style={styles.root}>
        {/* FestivalProvider wraps everything so song/image sync kicks off
            immediately in the background — even while the user is on the
            intro carousel. */}
        <FestivalProvider>
          <TransitionManager />
        </FestivalProvider>
      </RootView>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  root: {
    flex: 1,
  },
});

export default App;
