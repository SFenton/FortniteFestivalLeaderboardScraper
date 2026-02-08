import React, {useRef, useState} from 'react';
import {
  Dimensions,
  Image,
  ImageSourcePropType,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import {useSafeAreaInsets} from 'react-native-safe-area-context';
import PagerView from 'react-native-pager-view';
import {SlidingRowsBackground} from '../ui/SlidingRowsBackground';

/**
 * Fortnite Festival signature purple tones.
 * Primary: #7B2FBE  –  vibrant purple from the Festival logo / key art.
 * Background: #1A0A2E  –  the near-black purple used behind stages.
 */
const COLORS = {
  background: '#1A0A2E',
  primary: '#7B2FBE',
  accent: '#9D4EDD',
  textPrimary: '#FFFFFF',
  textSecondary: 'rgba(255,255,255,0.7)',
  dot: 'rgba(255,255,255,0.35)',
  dotActive: '#FFFFFF',
  buttonBg: '#7B2FBE',
};

// ── Intro page images (bundled assets) ──────────────────────────────
// Place four screenshot PNGs in  src/assets/intro/
//   intro_songs.png, intro_suggestions.png, intro_statistics.png, intro_detail.png
const introImages: ImageSourcePropType[] = [
  require('../assets/intro/intro_songs.png'),
  require('../assets/intro/intro_suggestions.png'),
  require('../assets/intro/intro_statistics.png'),
  require('../assets/intro/intro_detail.png'),
];

interface IntroPage {
  title: string;
  description: string;
  image: ImageSourcePropType;
}

const PAGES: IntroPage[] = [
  {
    title: 'Browse Every Song',
    description:
      'Explore the full catalog of songs with artwork and details.',
    image: introImages[0],
  },
  {
    title: 'Personalized Suggestions',
    description:
      'Discover songs you haven\'t played yet, songs you could score even higher on, and more, powered by a variety of library and score information.',
    image: introImages[1],
  },
  {
    title: 'Track Your Statistics',
    description:
      'See how you rank among the community.',
    image: introImages[2],
  },
  {
    title: 'Dive Into Song Details',
    description:
      'View scores, rankings, and star ratings for every track.',
    image: introImages[3],
  },
];

const {width: SCREEN_WIDTH} = Dimensions.get('window');
const IMAGE_WIDTH = SCREEN_WIDTH * 0.55;

type Props = {
  onContinue: () => void;
};

/**
 * Onboarding carousel shown on first launch (or after data clear).
 * Song + image sync kicks off in the background while the user swipes.
 */
export function IntroScreen({onContinue}: Props) {
  const pagerRef = useRef<PagerView>(null);
  const [currentPage, setCurrentPage] = useState(0);
  const insets = useSafeAreaInsets();

  const isLastPage = currentPage === PAGES.length - 1;

  const goNext = () => {
    if (isLastPage) {
      onContinue();
    } else {
      pagerRef.current?.setPage(currentPage + 1);
    }
  };

  const goBack = () => {
    if (currentPage > 0) {
      pagerRef.current?.setPage(currentPage - 1);
    }
  };

  return (
    <View style={[styles.container, {paddingTop: insets.top, paddingBottom: insets.bottom}]}>
      {/* Animated album art mosaic — activates when ≥10 images are cached */}
      <SlidingRowsBackground />

      {/* Skip row at the top */}
      <View style={styles.skipRow}>
        {!isLastPage ? (
          <Pressable
            style={({pressed}) => [styles.skipButton, pressed && {opacity: 0.6}]}
            onPress={onContinue}
          >
            <Text style={styles.skipText}>Skip</Text>
          </Pressable>
        ) : null}
      </View>

      <PagerView
        ref={pagerRef}
        style={styles.pager}
        initialPage={0}
        onPageSelected={e => setCurrentPage(e.nativeEvent.position)}
      >
        {PAGES.map((page, index) => (
          <View key={index} style={styles.page}>
            <View style={styles.imageContainer}>
              <Image
                source={page.image}
                style={styles.image}
                resizeMode="contain"
              />
            </View>

            <View style={styles.textContainer}>
              <Text style={styles.title}>{page.title}</Text>
              <Text style={styles.description}>{page.description}</Text>
            </View>
          </View>
        ))}
      </PagerView>

      {/* Pagination dots */}
      <View style={styles.dotsContainer}>
        {PAGES.map((_, index) => (
          <View
            key={index}
            style={[
              styles.dot,
              currentPage === index && styles.dotActive,
            ]}
          />
        ))}
      </View>

      {/* Navigation buttons */}
      <View style={styles.buttonsRow}>
        {currentPage > 0 ? (
          <Pressable
            style={({pressed}) => [
              styles.backButton,
              pressed && styles.buttonPressed,
            ]}
            onPress={goBack}
          >
            <Text style={styles.backButtonText}>Back</Text>
          </Pressable>
        ) : (
          <View style={styles.buttonSpacer} />
        )}

        <Pressable
          style={({pressed}) => [
            styles.nextButton,
            pressed && styles.buttonPressed,
          ]}
          onPress={goNext}
        >
          <Text style={styles.nextButtonText}>
            {isLastPage ? 'Start' : 'Next'}
          </Text>
        </Pressable>
      </View>

    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: COLORS.background,
  },
  pager: {
    flex: 1,
  },
  page: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: 32,
  },
  imageContainer: {
    flex: 0.66,
    width: IMAGE_WIDTH,
    alignSelf: 'center',
    borderRadius: 20,
    overflow: 'hidden',
    justifyContent: 'center',
    // Subtle shadow under the phone screenshot
    ...Platform.select({
      ios: {
        shadowColor: '#000',
        shadowOffset: {width: 0, height: 8},
        shadowOpacity: 0.4,
        shadowRadius: 16,
      },
      android: {
        elevation: 12,
      },
    }),
  },
  image: {
    width: '100%',
    flex: 1,
  },
  textContainer: {
    flex: 0.34,
    alignItems: 'center',
    justifyContent: 'flex-start',
    paddingTop: 24,
  },
  title: {
    fontSize: 26,
    fontWeight: '700',
    color: COLORS.textPrimary,
    textAlign: 'center',
    marginBottom: 12,
  },
  description: {
    fontSize: 15,
    lineHeight: 22,
    color: COLORS.textPrimary,
    textAlign: 'center',
    paddingHorizontal: 8,
  },

  // ── Dots ──────────────────────────────────────────────────────────
  dotsContainer: {
    flexDirection: 'row',
    justifyContent: 'center',
    alignItems: 'center',
    paddingVertical: 16,
  },
  dot: {
    width: 8,
    height: 8,
    borderRadius: 4,
    backgroundColor: COLORS.dot,
    marginHorizontal: 5,
  },
  dotActive: {
    backgroundColor: COLORS.dotActive,
    width: 24,
    borderRadius: 4,
  },

  // ── Buttons ───────────────────────────────────────────────────────
  buttonsRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingHorizontal: 24,
    paddingBottom: 12,
  },
  backButton: {
    width: '33%',
    backgroundColor: '#223047',
    paddingVertical: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#2B3B55',
    alignItems: 'center',
    justifyContent: 'center',
  },
  backButtonText: {
    fontSize: 16,
    fontWeight: '800',
    color: '#FFFFFF',
  },
  nextButton: {
    width: '33%',
    backgroundColor: '#7C3AED',
    paddingVertical: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#7C3AED',
    alignItems: 'center',
    justifyContent: 'center',
  },
  nextButtonText: {
    fontSize: 16,
    fontWeight: '800',
    color: '#FFFFFF',
  },
  buttonPressed: {
    opacity: 0.85,
  },
  buttonSpacer: {
    width: '33%',
  },

  // ── Skip ──────────────────────────────────────────────────────────
  skipRow: {
    flexDirection: 'row',
    justifyContent: 'flex-end',
    paddingHorizontal: 24,
    paddingVertical: 8,
    minHeight: 40,
  },
  skipButton: {
    padding: 8,
  },
  skipText: {
    fontSize: 15,
    fontWeight: '500',
    color: COLORS.textPrimary,
  },
});
