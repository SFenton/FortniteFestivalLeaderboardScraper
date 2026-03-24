/* eslint-disable react/forbid-dom-props -- stagger animation requires inline style */
import { useMemo, useState, useEffect } from 'react';
import SongInfo from '../../../../components/songs/metadata/SongInfo';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useFestival } from '../../../../contexts/FestivalContext';
import { useShop } from '../../../../contexts/ShopContext';
import { Layout, FADE_DURATION, STAGGER_INTERVAL } from '@festival/theme';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { fitRows } from '../../../../hooks/data/useDemoSongs';
import css from './ShopHighlightDemo.module.css';
import rowCss from './SongRowDemo.module.css';

const ROW_H = Layout.demoRowHeight;
const ROW_H_MOBILE = Layout.demoRowMobileHeight;

/**
 * Demo for the Songs FRE shop-highlight slide.
 * Builds an alternating shop/not/shop/not pattern from real data.
 */
/* v8 ignore start -- demo component requires FestivalContext + ShopContext + SlideHeightContext */
export default function ShopHighlightDemo() {
  const isMobile = useIsMobile();
  const { state: { songs } } = useFestival();
  const { shopSongIds } = useShop();
  const slideH = useSlideHeight();

  const rows = useMemo(() => {
    if (!shopSongIds || shopSongIds.size === 0 || songs.length === 0) return [];
    const shopSongs = songs.filter(s => shopSongIds.has(s.songId) && s.albumArt);
    const nonShopSongs = songs.filter(s => !shopSongIds.has(s.songId) && s.albumArt);
    if (shopSongs.length === 0 || nonShopSongs.length === 0) return [];

    const rh = isMobile ? ROW_H_MOBILE : ROW_H;
    const maxRows = slideH ? fitRows(slideH, rh) : 3;
    const result: { song: typeof songs[0]; inShop: boolean }[] = [];
    let si = 0, ni = 0;
    for (let i = 0; i < maxRows; i++) {
      if (i % 2 === 0 && si < shopSongs.length) {
        result.push({ song: shopSongs[si++]!, inShop: true });
      } else if (ni < nonShopSongs.length) {
        result.push({ song: nonShopSongs[ni++]!, inShop: false });
      } else if (si < shopSongs.length) {
        result.push({ song: shopSongs[si++]!, inShop: true });
      }
    }
    return result;
  }, [songs, shopSongIds, isMobile, slideH]);

  const [initialDone, setInitialDone] = useState(false);
  /* v8 ignore start — stagger timer */
  useEffect(() => {
    if (rows.length === 0) return;
    const ms = Math.max(0, rows.length - 1) * STAGGER_INTERVAL + FADE_DURATION + 100;
    const id = setTimeout(() => setInitialDone(true), ms);
    return () => clearTimeout(id);
  }, [rows.length]);
  /* v8 ignore stop */

  if (rows.length === 0) return null;

  return (
    <div className={css.list}>
      {rows.map(({ song, inShop }, i) => {
        const baseClass = isMobile ? rowCss.rowMobile : rowCss.row;
        const className = inShop ? `${baseClass} ${css.shopRow}` : baseClass;
        return (
          <div key={i} className={className}
            style={initialDone
              ? undefined
              : { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${i * STAGGER_INTERVAL}ms forwards` }
            }
          >
            {isMobile ? (
              <div className={css.mobileTopRow}>
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
