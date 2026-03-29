/* eslint-disable react/forbid-dom-props -- stagger animation requires inline style */
import { useMemo, useState, useEffect } from 'react';
import SongInfo from '../../../../components/songs/metadata/SongInfo';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useFestival } from '../../../../contexts/FestivalContext';
import { useShop } from '../../../../contexts/ShopContext';
import { Layout, FADE_DURATION, STAGGER_INTERVAL, Gap, CssValue, flexColumn } from '@festival/theme';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { fitRows } from '../../../../hooks/data/useDemoSongs';
import anim from '../../../../styles/animations.module.css';
import { songRow, songRowMobile, mobileTopRow } from '../../../../styles/songRowStyles';

const ROW_H = Layout.demoRowHeight;
const ROW_H_MOBILE = Layout.demoRowMobileHeight;

/**
 * Demo for the Songs FRE "leaving tomorrow" slide.
 * Shows three states: red-pulsing (leaving), blue-pulsing (in shop), and plain (not in shop).
 */
/* v8 ignore start -- demo component requires FestivalContext + ShopContext + SlideHeightContext */
export default function LeavingTomorrowDemo() {
  const isMobile = useIsMobile();
  const { state: { songs } } = useFestival();
  const { shopSongIds, leavingTomorrowIds } = useShop();
  const slideH = useSlideHeight();

  const rows = useMemo(() => {
    if (!shopSongIds || shopSongIds.size === 0 || songs.length === 0) return [];
    const leavingSongs = songs.filter(s => leavingTomorrowIds?.has(s.songId) && s.albumArt);
    const shopSongs = songs.filter(s => shopSongIds.has(s.songId) && !leavingTomorrowIds?.has(s.songId) && s.albumArt);
    const nonShopSongs = songs.filter(s => !shopSongIds.has(s.songId) && s.albumArt);

    const rh = isMobile ? ROW_H_MOBILE : ROW_H;
    const maxRows = slideH ? fitRows(slideH, rh) : 3;
    const result: { song: typeof songs[0]; state: 'leaving' | 'shop' | 'none' }[] = [];
    let li = 0, si = 0, ni = 0;

    // Pattern: leaving, shop, none, leaving, shop, none...
    for (let i = 0; i < maxRows; i++) {
      const phase = i % 3;
      if (phase === 0 && li < leavingSongs.length) {
        result.push({ song: leavingSongs[li++]!, state: 'leaving' });
      } else if (phase === 1 && si < shopSongs.length) {
        result.push({ song: shopSongs[si++]!, state: 'shop' });
      } else if (ni < nonShopSongs.length) {
        result.push({ song: nonShopSongs[ni++]!, state: 'none' });
      } else if (si < shopSongs.length) {
        result.push({ song: shopSongs[si++]!, state: 'shop' });
      } else if (li < leavingSongs.length) {
        result.push({ song: leavingSongs[li++]!, state: 'leaving' });
      }
    }
    return result;
  }, [songs, shopSongIds, leavingTomorrowIds, isMobile, slideH]);

  const [initialDone, setInitialDone] = useState(false);
  useEffect(() => {
    if (rows.length === 0) return;
    const ms = Math.max(0, rows.length - 1) * STAGGER_INTERVAL + FADE_DURATION + 100;
    const id = setTimeout(() => setInitialDone(true), ms);
    return () => clearTimeout(id);
  }, [rows.length]);

  if (rows.length === 0) return null;

  return (
    <div style={{ width: CssValue.full, ...flexColumn, gap: Gap.sm }}>
      {rows.map(({ song, state }, i) => {
        const baseStyle = isMobile ? songRowMobile : songRow;
        const className = state === 'leaving' ? anim.shopHighlightRed
          : state === 'shop' ? anim.shopHighlight
          : undefined;
        return (
          <div key={i} className={className}
            style={initialDone
              ? baseStyle
              : { ...baseStyle, opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${i * STAGGER_INTERVAL}ms forwards` }}
          >
            {isMobile ? (
              <div style={mobileTopRow}>
                <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} />
              </div>
            ) : (
              <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} />
            )}
          </div>
        );
      })}
    </div>
  );
}
/* v8 ignore stop */
