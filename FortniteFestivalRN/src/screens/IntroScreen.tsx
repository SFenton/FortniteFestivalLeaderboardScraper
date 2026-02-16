import React, {useCallback, useEffect, useMemo, useRef, useState} from 'react';
import {
  Animated,
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
import {
  FrostedSurface,
  InstrumentCard,
  type InstrumentCardData,
  StatisticsInstrumentCard,
  type StatisticsCardData,
  SongRow,
  type SongRowDisplayData,
  MAUI_STATUS_COLORS,
  SuggestionCard,
} from '@festival/ui';
import {useFestival} from '@festival/contexts';
import type {SuggestionCategory, SuggestionSongItem, InstrumentShowSettings, InstrumentKey, LeaderboardData, Song} from '@festival/core';

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

/** Duration (ms) of each element's fade-in. */
const FADE_DURATION = 500;
/** Delay (ms) between successive element fade-ins. */
const STAGGER_DELAY = 250;
/** Maximum animated elements on any single page (3 song rows + text). */
const MAX_FADE_ITEMS = 5;

/** Index of the "Browse Every Song" carousel page. */
const BROWSE_SONGS_PAGE_INDEX = 0;

/** Index of the "Personalized Suggestions" carousel page. */
const SUGGESTIONS_PAGE_INDEX = 1;

/** Index of the "Track Your Statistics" carousel page. */
const STATISTICS_PAGE_INDEX = 2;

/** Index of the "Dive Into Song Details" carousel page. */
const SONG_DETAILS_PAGE_INDEX = 3;

/** All-instrument chip visuals: lead/bass gold, drums/vocals green, pro lead/bass red. */
const ALL_INSTRUMENT_CHIPS = [
  {instrumentKey: 'guitar' as const, ...MAUI_STATUS_COLORS.fullCombo},
  {instrumentKey: 'bass' as const, ...MAUI_STATUS_COLORS.fullCombo},
  {instrumentKey: 'drums' as const, ...MAUI_STATUS_COLORS.hasScore},
  {instrumentKey: 'vocals' as const, ...MAUI_STATUS_COLORS.hasScore},
  {instrumentKey: 'pro_guitar' as const, ...MAUI_STATUS_COLORS.noScore},
  {instrumentKey: 'pro_bass' as const, ...MAUI_STATUS_COLORS.noScore},
];

/** Number of Epic Games songs to show on the Browse page. */
const BROWSE_SONG_COUNT = 3;

/** Hardcoded song rows used when the song cache is empty. */
const FALLBACK_SONG_ROWS: SongRowDisplayData[] = [
  {title: 'Butter Barn Hoedown', artist: 'Epic Games', year: 2021, instruments: ALL_INSTRUMENT_CHIPS},
  {title: 'OG (Future Remix)', artist: 'Epic Games', year: 2020, instruments: ALL_INSTRUMENT_CHIPS},
  {title: 'Switch Up', artist: 'Epic Games', year: 2020, instruments: ALL_INSTRUMENT_CHIPS},
];

/** Instrument keys assigned to each of the 3 fake star_gains rows. */
const INTRO_SUGGESTION_INSTRUMENTS: InstrumentKey[] = ['guitar', 'drums', 'bass'];
/** Star counts for each fake star_gains row. */
const INTRO_SUGGESTION_STARS = [5, 4, 5];
/** Fake scores for each fake star_gains row. */
const INTRO_SUGGESTION_SCORES = [87_500, 72_000, 91_200];

/** Settings with all instruments enabled (for the fake suggestion card). */
const ALL_INSTRUMENTS_SETTINGS: InstrumentShowSettings = {
  showLead: true, showBass: true, showDrums: true,
  showVocals: true, showProLead: true, showProBass: true,
};

/** Build a minimal fake LeaderboardData with a single instrument's score. */
function fakeLeaderboard(songId: string, instrumentKey: InstrumentKey, numStars: number, maxScore: number): LeaderboardData {
  const tracker = {initialized: true, numStars, maxScore, isFullCombo: false, percentHit: 95, seasonAchieved: 0};
  return {songId, [instrumentKey]: tracker} as unknown as LeaderboardData;
}

/** Hardcoded suggestion data used when the song cache is empty. */
const FALLBACK_SUGGESTIONS: {category: SuggestionCategory; songById: Map<string, Song>; scoresIndex: Record<string, LeaderboardData>} = (() => {
  const songById = new Map<string, Song>();
  const scoresIndex: Record<string, LeaderboardData> = {};
  const items: SuggestionSongItem[] = FALLBACK_SONG_ROWS.map((row, i) => {
    const id = `fallback_${i}`;
    const instr = INTRO_SUGGESTION_INSTRUMENTS[i % INTRO_SUGGESTION_INSTRUMENTS.length];
    scoresIndex[id] = fakeLeaderboard(id, instr, INTRO_SUGGESTION_STARS[i] ?? 5, INTRO_SUGGESTION_SCORES[i] ?? 80_000);
    return {songId: id, title: row.title, artist: row.artist, stars: INTRO_SUGGESTION_STARS[i], instrumentKey: instr};
  });
  return {
    category: {key: 'star_gains', title: 'Easy Star Gains', description: 'Hit a new high score to get even more stars on these songs!', songs: items},
    songById,
    scoresIndex,
  };
})();

/** Fake data shown on the intro carousel's Song Details page. */
const INTRO_INSTRUMENT_DATA: InstrumentCardData = {
  key: 'guitar',
  name: 'Lead',
  hasScore: true,
  isFullCombo: true,
  starsCount: 6,
  rawDifficulty: 4,
  scoreDisplay: '100,000',
  percentDisplay: '100%',
  seasonDisplay: 'S13',
  percentileDisplay: 'Top 1%',
  rankOutOfDisplay: '#1 / 100,000',
  isTop5Percentile: true,
};

/** Fake data shown on the intro carousel's Statistics page. */
const INTRO_STATISTICS_DATA: StatisticsCardData = {
  instrumentKey: 'guitar',
  instrumentLabel: 'Lead',
  totalSongsInLibrary: 500,
  songsPlayed: 500,
  completionPercent: 100,
  fcCount: 500,
  fcPercent: 100,
  goldStarCount: 500,
  fiveStarCount: 0,
  fourStarCount: 0,
  averageAccuracy: 100,
  bestAccuracy: 100,
  averageStars: 6,
  bestRank: 1,
  bestRankFormatted: '#1',
  weightedPercentileFormatted: 'Top 1%',
  top1PercentCount: 450,
  top5PercentCount: 40,
  top10PercentCount: 5,
  top25PercentCount: 3,
  top50PercentCount: 2,
  below50PercentCount: 0,
};

type Props = {
  onContinue: () => void;
  /** Fired once the carousel has laid out and is ready to be revealed. */
  onReady?: () => void;
  /** True once the carousel fade-in animation is complete. Staggers wait for this. */
  revealed?: boolean;
};

/**
 * Onboarding carousel shown on first launch (or after data clear).
 * Song + image sync kicks off in the background while the user swipes.
 */
export function IntroScreen({onContinue, onReady, revealed = false}: Props) {
  const pagerRef = useRef<PagerView>(null);
  const [currentPage, setCurrentPage] = useState(0);
  const insets = useSafeAreaInsets();
  const reportedReady = useRef(false);

  const {state: {songs, isReady: syncFinished}} = useFestival();

  /** True once songs are available (from cache or sync) or sync has finished (even if empty). */
  const songsReady = songs.length > 0 || syncFinished;
  const laidOut = useRef(false);

  /** Up to 3 "Epic Games" songs pulled from the live song cache, or hardcoded fallback. */
  const epicGamesSongRows = useMemo<SongRowDisplayData[]>(() => {
    const epicSongs = songs.filter(s => s.track.an === 'Epic Games');
    if (epicSongs.length === 0) return FALLBACK_SONG_ROWS;
    return epicSongs.slice(0, BROWSE_SONG_COUNT).map(s => ({
      title: s.track.tt ?? s._title ?? s.track.su,
      artist: s.track.an ?? '',
      year: s.track.ry,
      imageUri: s.imagePath ?? s.track.au,
      instruments: ALL_INSTRUMENT_CHIPS,
    }));
  }, [songs]);

  /** True when using hardcoded fallback data (no real songs available). */
  const usingFallback = epicGamesSongRows === FALLBACK_SONG_ROWS;

  // ── Staggered fade-in animation for each page ──────────────────────
  const fadeAnims = useRef(
    PAGES.map(() =>
      Array.from({length: MAX_FADE_ITEMS}, () => new Animated.Value(0)),
    ),
  ).current;

  /** Tracks the element count that was animated per page (re-runs if count grows). */
  const animatedCounts = useRef(new Map<number, number>()).current;

  useEffect(() => {
    if (!revealed) return; // wait for carousel fade-in to finish

    const browseRowCount =
      currentPage === BROWSE_SONGS_PAGE_INDEX ? epicGamesSongRows.length : 0;
    const count = browseRowCount > 0 ? browseRowCount + 1 : 2;

    const prev = animatedCounts.get(currentPage) ?? 0;
    if (prev >= count) return; // already animated this many or more
    animatedCounts.set(currentPage, count);

    const anims = fadeAnims[currentPage];

    // On first visit, reset everything and stagger from the top.
    // If we're only here because more items appeared (e.g. songs loaded),
    // snap already-visible items to 1 and only animate the new ones.
    if (prev === 0) {
      anims.forEach(a => a.setValue(0));
    } else {
      anims.slice(0, prev).forEach(a => a.setValue(1));
      anims.slice(prev, count).forEach(a => a.setValue(0));
    }

    Animated.stagger(
      STAGGER_DELAY,
      anims.slice(prev, count).map(a =>
        Animated.timing(a, {
          toValue: 1,
          duration: FADE_DURATION,
          useNativeDriver: true,
        }),
      ),
    ).start();
  }, [currentPage, epicGamesSongRows.length, fadeAnims, animatedCounts, revealed]);

  /** No-op handler – intro screen song rows are non-interactive. */
  const noOpOpenSong = useCallback(() => {}, []);

  /** Fake star_gains suggestion card data for the Personalized Suggestions page. */
  const suggestionsPageData = useMemo(() => {
    const epicSongs = songs.filter(s => s.track.an === 'Epic Games').slice(0, BROWSE_SONG_COUNT);
    if (epicSongs.length === 0) return FALLBACK_SUGGESTIONS;

    const songById = new Map<string, Song>();
    const scoresIndex: Record<string, LeaderboardData> = {};
    const items: SuggestionSongItem[] = [];

    epicSongs.forEach((s, i) => {
      const id = s.track.su;
      songById.set(id, s);
      const instr = INTRO_SUGGESTION_INSTRUMENTS[i % INTRO_SUGGESTION_INSTRUMENTS.length];
      scoresIndex[id] = fakeLeaderboard(id, instr, INTRO_SUGGESTION_STARS[i] ?? 5, INTRO_SUGGESTION_SCORES[i] ?? 80_000);
      items.push({
        songId: id,
        title: s.track.tt ?? s._title ?? s.track.su,
        artist: s.track.an ?? '',
        stars: INTRO_SUGGESTION_STARS[i],
        instrumentKey: instr,
      });
    });

    const category: SuggestionCategory = {
      key: 'star_gains',
      title: 'Easy Star Gains',
      description: 'Hit a new high score to get even more stars on these songs!',
      songs: items,
    };

    return {category, songById, scoresIndex};
  }, [songs]);

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

  // Fire onReady only once the view has laid out AND songs are available.
  useEffect(() => {
    if (!laidOut.current || !songsReady || reportedReady.current) return;
    reportedReady.current = true;
    // Small delay lets child views finish their first paint
    setTimeout(() => onReady?.(), 100);
  }, [songsReady, onReady]);

  return (
    <View
      style={[styles.container, {paddingTop: insets.top, paddingBottom: insets.bottom}]}
      onLayout={() => {
        laidOut.current = true;
        if (songsReady && !reportedReady.current) {
          reportedReady.current = true;
          setTimeout(() => onReady?.(), 100);
        }
      }}>

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
        {PAGES.map((page, index) => {
          // Text area is always the last animated element on the page.
          const textFadeIdx =
            index === BROWSE_SONGS_PAGE_INDEX && epicGamesSongRows.length > 0
              ? epicGamesSongRows.length
              : 1;

          return (
            <View key={index} style={styles.page}>
              <View style={index === SONG_DETAILS_PAGE_INDEX || index === STATISTICS_PAGE_INDEX || index === BROWSE_SONGS_PAGE_INDEX || index === SUGGESTIONS_PAGE_INDEX ? styles.cardContainer : styles.imageContainer}>
                {index === SONG_DETAILS_PAGE_INDEX ? (
                  <Animated.View style={{opacity: fadeAnims[index][0]}}>
                    <InstrumentCard data={INTRO_INSTRUMENT_DATA} />
                  </Animated.View>
                ) : index === STATISTICS_PAGE_INDEX ? (
                  <Animated.View style={{opacity: fadeAnims[index][0]}}>
                    <StatisticsInstrumentCard data={INTRO_STATISTICS_DATA} compact />
                  </Animated.View>
                ) : index === BROWSE_SONGS_PAGE_INDEX && epicGamesSongRows.length > 0 ? (
                  <View style={styles.songRowsContainer}>
                    {epicGamesSongRows.map((row, i) => (
                      <Animated.View key={i} style={{opacity: fadeAnims[index][i]}}>
                        <SongRow data={row} compact />
                      </Animated.View>
                    ))}
                  </View>
                ) : index === SUGGESTIONS_PAGE_INDEX && suggestionsPageData ? (
                  <Animated.View style={{opacity: fadeAnims[index][0]}}>
                    <SuggestionCard
                      cat={suggestionsPageData.category}
                      useCompactLayout
                      hideArt={usingFallback}
                      songById={suggestionsPageData.songById}
                      scoresIndex={suggestionsPageData.scoresIndex}
                      instrumentQuerySettings={ALL_INSTRUMENTS_SETTINGS}
                      onOpenSong={noOpOpenSong}
                    />
                  </Animated.View>
                ) : (
                  <Animated.View style={{opacity: fadeAnims[index][0]}}>
                    <Image
                      source={page.image}
                      style={styles.image}
                      resizeMode="contain"
                    />
                  </Animated.View>
                )}
              </View>

              <View style={styles.spacer} />

              <Animated.View style={[styles.textArea, {opacity: fadeAnims[index][textFadeIdx]}]}>
                <FrostedSurface style={styles.textContainer} tint="dark" intensity={18}>
                  <Text style={styles.title}>{page.title}</Text>
                  <Text style={styles.description}>{page.description}</Text>
                </FrostedSurface>
              </Animated.View>
            </View>
          );
        })}
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
    backgroundColor: 'transparent',
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
    flex: 0.6,
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
  cardContainer: {
    flex: 0.6,
    width: '90%',
    maxWidth: 600,
    alignSelf: 'center',
    justifyContent: 'center',
  },
  songRowsContainer: {
    width: '100%',
    justifyContent: 'center',
  },
  image: {
    width: '100%',
    flex: 1,
  },
  textArea: {
    flex: 0.3,
    width: '100%',
    maxWidth: 600,
    alignSelf: 'center',
    justifyContent: 'flex-start',
  },
  textContainer: {
    width: '100%',
    alignItems: 'center',
    paddingVertical: 24,
    borderRadius: 16,
    overflow: 'hidden',
    marginHorizontal: 8,
  },
  spacer: {
    flex: 0.1,
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
