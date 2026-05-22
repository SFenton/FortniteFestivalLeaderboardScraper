/* eslint-disable react/forbid-dom-props -- stagger animation requires inline style */
import { useMemo, useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import SongInfo from '../../../../components/songs/metadata/SongInfo';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useFestival } from '../../../../contexts/FestivalContext';
import { useShop } from '../../../../contexts/ShopContext';
import { Layout, FADE_DURATION, STAGGER_INTERVAL, Gap, CssValue, flexColumn, Font, Colors } from '@festival/theme';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { fitRows } from '../../../../hooks/data/useDemoSongs';
import anim from '../../../../styles/animations.module.css';
import { songRow, songRowMobile, mobileTopRow } from '../../../../styles/songRowStyles';

const ROW_H = Layout.demoRowHeight;
const ROW_H_MOBILE = Layout.demoRowMobileHeight;

/* v8 ignore start -- demo component requires FestivalContext + ShopContext + SlideHeightContext */
export default function NewInShopDemo() {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const { state: { songs } } = useFestival();
  const { shopSongIds, newShopIds } = useShop();
  const slideH = useSlideHeight();

  const rows = useMemo(() => {
    if (!shopSongIds || shopSongIds.size === 0 || songs.length === 0) return [];

    const newSongs = songs.filter(s => newShopIds?.has(s.songId) && s.albumArt);
    const shopSongs = songs.filter(s => shopSongIds.has(s.songId) && !newShopIds?.has(s.songId) && s.albumArt);
    const nonShopSongs = songs.filter(s => !shopSongIds.has(s.songId) && s.albumArt);

    const rh = isMobile ? ROW_H_MOBILE : ROW_H;
    const maxRows = slideH ? fitRows(slideH, rh) : 3;
    const result: { song: typeof songs[0]; state: 'new' | 'shop' | 'none' }[] = [];
    let ni = 0, si = 0, oi = 0;

    for (let i = 0; i < maxRows; i++) {
      const phase = i % 3;
      if (phase === 0) {
        if (ni < newSongs.length) result.push({ song: newSongs[ni++]!, state: 'new' });
        else if (si < shopSongs.length) result.push({ song: shopSongs[si++]!, state: 'new' });
        else if (oi < nonShopSongs.length) result.push({ song: nonShopSongs[oi++]!, state: 'new' });
      } else if (phase === 1) {
        if (si < shopSongs.length) result.push({ song: shopSongs[si++]!, state: 'shop' });
        else if (oi < nonShopSongs.length) result.push({ song: nonShopSongs[oi++]!, state: 'none' });
      } else if (oi < nonShopSongs.length) {
        result.push({ song: nonShopSongs[oi++]!, state: 'none' });
      }
    }

    return result;
  }, [songs, shopSongIds, newShopIds, isMobile, slideH]);

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
        const className = state === 'new' ? anim.shopHighlightGold : state === 'shop' ? anim.shopHighlight : undefined;
        return (
          <div key={i} className={className}
            style={initialDone
              ? baseStyle
              : { ...baseStyle, opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${i * STAGGER_INTERVAL}ms forwards` }}
          >
            {isMobile ? (
              <div style={mobileTopRow}>
                <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} minWidth={0} />
              </div>
            ) : (
              <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} />
            )}
          </div>
        );
      })}
      <span style={initialDone
        ? { fontSize: Font.xs, color: Colors.textTertiary, fontStyle: 'italic' as const, marginTop: Gap.xs }
        : { fontSize: Font.xs, color: Colors.textTertiary, fontStyle: 'italic' as const, marginTop: Gap.xs, opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${rows.length * STAGGER_INTERVAL}ms forwards` }}>
        {t('firstRun.demoSimulated', 'Some demo data may be simulated.')}
      </span>
    </div>
  );
}
/* v8 ignore stop */