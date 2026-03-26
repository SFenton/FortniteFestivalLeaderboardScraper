/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * First-run demo: Top songs list using production PlayerSongRow components.
 * Pulls real songs from the catalog via useDemoSongs and assigns static percentiles.
 */
import { useMemo, type CSSProperties } from 'react';
import { Layout, Opacity, CssValue, PointerEvents, CssProp, STAGGER_INTERVAL, transition } from '@festival/theme';
import PlayerSongRow from '../../components/PlayerSongRow';
import { useDemoSongs, FADE_MS } from '../../../../hooks/data/useDemoSongs';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { topSongsStyles } from '../../components/TopSongsSection';

/* v8 ignore start -- NOOP is passed as prop but never invoked in test (pointerEvents: none) */
const NOOP = (e: React.MouseEvent) => e.preventDefault();
/* v8 ignore stop */

/** Static percentile values assigned to demo rows. */
// eslint-disable-next-line no-magic-numbers -- static demo percentile values
const DEMO_PERCENTILES = [1.2, 3.5, 7.8, 14.2, 22.6, 35.1, 48.9];

export default function TopSongsDemo() {
  const isMobile = useIsMobile();
  const h = useSlideHeight();

  const rowHeight = Layout.songRowHeight;
  const availableForRows = h ?? rowHeight * 4;
  const maxRows = Math.max(1, Math.floor(availableForRows / rowHeight));

  const { rows, fadingIdx, initialDone } = useDemoSongs({
    rowHeight,
    mobileRowHeight: rowHeight,
    isMobile,
  });

  const visible = rows.slice(0, maxRows);
  const s = useStyles(visible.length, fadingIdx, initialDone);

  return (
    <div style={s.wrapper}>
      <div style={topSongsStyles.songList}>
        {visible.map((song, i) => (
            <div key={i} style={s.rows[i]}>
              <PlayerSongRow
                songId={`demo-${i}`}
                href="#"
                albumArt={song.albumArt}
                title={song.title}
                artist={song.artist}
                year={song.year}
                percentile={DEMO_PERCENTILES[i % DEMO_PERCENTILES.length]}
                onClick={NOOP}
              />
            </div>
        ))}
      </div>
    </div>
  );
}

function useStyles(count: number, fadingIdx: Set<number>, initialDone: boolean) {
  return useMemo(() => {
    const rows: CSSProperties[] = [];
    for (let i = 0; i < count; i++) {
      rows.push(initialDone
        ? { opacity: fadingIdx.has(i) ? Opacity.none : 1, transition: transition(CssProp.opacity, FADE_MS) }
        : { opacity: Opacity.none, animation: `fadeInUp ${FADE_MS}ms ease-out ${(i + 1) * STAGGER_INTERVAL}ms forwards` },
      );
    }
    return {
      wrapper: { width: CssValue.full, pointerEvents: PointerEvents.none } as CSSProperties,
      rows,
    };
  }, [count, fadingIdx, initialDone]);
}
