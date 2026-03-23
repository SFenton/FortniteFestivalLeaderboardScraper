/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * First-run demo: Top songs list using production PlayerSongRow components.
 * Pulls real songs from the catalog via useDemoSongs and assigns static percentiles.
 */
import { Layout, STAGGER_INTERVAL } from '@festival/theme';
import PlayerSongRow from '../../components/PlayerSongRow';
import { useDemoSongs, FADE_MS } from '../../../../hooks/data/useDemoSongs';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import css from '../../components/TopSongsSection.module.css';

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

  return (
    <div style={{ width: '100%', pointerEvents: 'none' }}>
      <div className={css.songList}>
        {visible.map((song, i) => {
          const fadeStyle = initialDone
            ? { opacity: fadingIdx.has(i) ? 0 : 1, transition: `opacity ${FADE_MS}ms ease` }
            : { opacity: 0, animation: `fadeInUp ${FADE_MS}ms ease-out ${(i + 1) * STAGGER_INTERVAL}ms forwards` };
          return (
            <div key={i} style={fadeStyle}>
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
          );
        })}
      </div>
    </div>
  );
}
