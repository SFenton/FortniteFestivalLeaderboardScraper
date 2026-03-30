/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Head-to-head song comparison rows using real songs.
 * Cycles through rivalry category headers and swaps song rows.
 * Rank data alternates evenly between player-winning and player-losing.
 */
import { useState, useEffect, useRef, useMemo, type CSSProperties } from 'react';
import type { RivalSongComparison } from '@festival/core/api/serverTypes';
import RivalSongRow from '../../components/RivalSongRow';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useDemoSongs } from '../../../../hooks/data/useDemoSongs';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import {
  Colors, Font, Weight, Gap, Opacity, CssValue, PointerEvents, flexColumn,
  FADE_DURATION,
} from '@festival/theme';

const ROW_HEIGHT = 130;
const LABEL_HEIGHT = 28;
const NOOP = () => {};

/** Category headers matching the real RivalryPage modes. */
const CATEGORIES = [
  'Closest Battles',
  'Almost Passed',
  'Slipping Away',
  'Barely Winning',
  'Pulling Forward',
  'Dominating Them',
] as const;

type RankRow = { userRank: number; rivalRank: number; userScore: number; rivalScore: number };

/**
 * Per-category rank/score data so the demo cards match the header.
 *
 * Closest Battles  — near-identical ranks and scores
 * Almost Passed    — rival slightly ahead (player close to overtaking)
 * Slipping Away    — rival pulling further ahead
 * Barely Winning   — player slightly ahead
 * Pulling Forward  — player has a solid lead
 * Dominating Them  — player far ahead
 */
const CATEGORY_RANK_DATA: Record<string, RankRow[]> = {
  'Closest Battles': [
    { userRank: 14, rivalRank: 15, userScore: 988000, rivalScore: 987500 },
    { userRank: 23, rivalRank: 22, userScore: 965000, rivalScore: 965800 },
    { userRank: 8, rivalRank: 9, userScore: 995200, rivalScore: 994900 },
    { userRank: 31, rivalRank: 30, userScore: 942000, rivalScore: 942600 },
  ],
  'Almost Passed': [
    { userRank: 18, rivalRank: 15, userScore: 971000, rivalScore: 978000 },
    { userRank: 12, rivalRank: 9, userScore: 986000, rivalScore: 992000 },
    { userRank: 26, rivalRank: 22, userScore: 950000, rivalScore: 958000 },
    { userRank: 35, rivalRank: 31, userScore: 930000, rivalScore: 938000 },
  ],
  'Slipping Away': [
    { userRank: 28, rivalRank: 12, userScore: 945000, rivalScore: 985000 },
    { userRank: 40, rivalRank: 18, userScore: 910000, rivalScore: 970000 },
    { userRank: 35, rivalRank: 15, userScore: 930000, rivalScore: 978000 },
    { userRank: 48, rivalRank: 22, userScore: 890000, rivalScore: 960000 },
  ],
  'Barely Winning': [
    { userRank: 15, rivalRank: 18, userScore: 978000, rivalScore: 971000 },
    { userRank: 9, rivalRank: 12, userScore: 992000, rivalScore: 986000 },
    { userRank: 22, rivalRank: 26, userScore: 958000, rivalScore: 950000 },
    { userRank: 31, rivalRank: 35, userScore: 938000, rivalScore: 930000 },
  ],
  'Pulling Forward': [
    { userRank: 8, rivalRank: 22, userScore: 994000, rivalScore: 960000 },
    { userRank: 5, rivalRank: 18, userScore: 998000, rivalScore: 970000 },
    { userRank: 12, rivalRank: 30, userScore: 986000, rivalScore: 940000 },
    { userRank: 10, rivalRank: 26, userScore: 990000, rivalScore: 952000 },
  ],
  'Dominating Them': [
    { userRank: 3, rivalRank: 45, userScore: 999000, rivalScore: 895000 },
    { userRank: 2, rivalRank: 38, userScore: 999500, rivalScore: 915000 },
    { userRank: 5, rivalRank: 52, userScore: 998000, rivalScore: 880000 },
    { userRank: 4, rivalRank: 60, userScore: 998500, rivalScore: 860000 },
  ],
};

export default function RivalsDetailDemo() {
  const h = useSlideHeight();
  const isMobile = useIsMobile();
  const s = useStyles();

  const { rows: demoSongs, fadingIdx } = useDemoSongs({
    rowHeight: ROW_HEIGHT,
    mobileRowHeight: ROW_HEIGHT,
    isMobile,
    autoSwap: true,
  });

  // Fade all elements out together, then restagger them in when songs swap.
  // When fadingIdx goes non-empty every element fades out; when it clears we
  // bump staggerKey so FadeIn wrappers remount with fresh stagger delays.
  const [headerIdx, setHeaderIdx] = useState(0);
  const [allFading, setAllFading] = useState(false);
  const [staggerKey, setStaggerKey] = useState(0);
  const headerIdxRef = useRef(0);
  const prevFadingRef = useRef(false);

  useEffect(() => {
    const isFading = fadingIdx.size > 0;
    const wasFading = prevFadingRef.current;
    prevFadingRef.current = isFading;

    if (isFading && !wasFading) {
      // Swap starting — fade everything out
      setAllFading(true);
    } else if (!isFading && wasFading) {
      // Swap complete — advance header and restagger everything in
      headerIdxRef.current = (headerIdxRef.current + 1) % CATEGORIES.length;
      setHeaderIdx(headerIdxRef.current);
      setStaggerKey(k => k + 1);
      setAllFading(false);
    }
  }, [fadingIdx]);

  const budget = h || 320;
  const maxRows = Math.max(1, Math.floor((budget - LABEL_HEIGHT - Gap.md) / (ROW_HEIGHT + Gap.sm)));

  const categoryLabel = CATEGORIES[headerIdx]!;
  const rankData = CATEGORY_RANK_DATA[categoryLabel]!;

  const visible = useMemo<(RivalSongComparison & { albumArt?: string })[]>(() =>
    demoSongs.slice(0, Math.min(maxRows, 4)).map((song, i) => {
      const rd = rankData[i % rankData.length]!;
      return {
        songId: song.title,
        title: song.title,
        artist: song.artist,
        instrument: 'Solo_Guitar',
        userRank: rd.userRank,
        rivalRank: rd.rivalRank,
        rankDelta: rd.rivalRank - rd.userRank,
        userScore: rd.userScore,
        rivalScore: rd.rivalScore,
        albumArt: song.albumArt,
      };
    }),
  [demoSongs, maxRows, rankData]);

  return (
    <div style={s.wrapper}>
      <FadeIn key={`hdr-${staggerKey}`} delay={allFading ? undefined : 0}>
        <span style={{ ...s.label, ...(allFading ? s.fading : s.visible) }}>
          {categoryLabel}
        </span>
      </FadeIn>
      <div style={s.list}>
        {visible.map((song, i) => (
          <FadeIn key={`${staggerKey}-${song.songId}`} delay={allFading ? undefined : (i + 1) * 100}>
            <div style={allFading ? s.fading : s.visible}>
              <RivalSongRow
                song={song}
                albumArt={song.albumArt}
                playerName="You"
                rivalName="KeyDrifter"
                onClick={NOOP}
                standalone
              />
            </div>
          </FadeIn>
        ))}
      </div>
    </div>
  );
}

function useStyles() {
  return useMemo(() => {
    const trans = `opacity ${FADE_DURATION}ms ease, transform ${FADE_DURATION}ms ease`;
    return {
      wrapper: {
        ...flexColumn,
        gap: Gap.md,
        width: CssValue.full,
        pointerEvents: PointerEvents.none,
      } as CSSProperties,
      label: {
        fontSize: Font.lg,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
      } as CSSProperties,
      list: {
        ...flexColumn,
        gap: Gap.sm,
      } as CSSProperties,
      visible: {
        transition: trans,
        opacity: 1,
        transform: 'translateY(0)',
      } as CSSProperties,
      fading: {
        transition: trans,
        opacity: Opacity.none,
        transform: 'translateY(4px)',
      } as CSSProperties,
    };
  }, []);
}
