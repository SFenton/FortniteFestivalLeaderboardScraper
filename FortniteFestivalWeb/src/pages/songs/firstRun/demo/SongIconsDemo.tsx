/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * First-run demo: song rows with real instrument status chips.
 * Auto-fits rows, rotates one every 5 s with fade-out/in.
 */
import { useMemo } from 'react';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentChip } from '../../../../components/display/InstrumentChip';
import SongInfo from '../../../../components/songs/metadata/SongInfo';
import { useIsMobileChrome } from '../../../../hooks/ui/useIsMobile';
import { useSettings, visibleInstruments } from '../../../../contexts/SettingsContext';
import { Layout } from '@festival/theme';
import { useDemoSongs } from '../../../../hooks/data/useDemoSongs';
import { DemoSongRow } from './DemoSongRow';
import css from './SongRowDemo.module.css';
import baseCss from '../../../../styles/songRow.module.css';

type DemoScore = { hasScore: boolean; isFC: boolean };

const ROW_HEIGHT_DESKTOP = Layout.demoRowHeight;
const ROW_HEIGHT_MOBILE = Layout.demoRowMobileIconsHeight;

/* v8 ignore start -- Deterministic hash whose branches depend on input bit patterns */
/** Generate a random score pattern per instrument (stable via title seed). */
function buildScores(title: string, keys: InstrumentKey[]): Record<string, DemoScore> {
  let h = 0;
  for (let i = 0; i < title.length; i++) h = ((h << 5) - h + title.charCodeAt(i)) | 0;
  const bits = Math.abs(h);
  const result: Record<string, DemoScore> = {};
  keys.forEach((key, i) => {
    const hasScore = ((bits >> (i * 2)) & 1) === 1;
    const isFC = hasScore && ((bits >> (i * 2 + 1)) & 1) === 1;
    result[key] = { hasScore, isFC };
  });
  return result;
}
/* v8 ignore stop */

function ChipRow({ scores, instruments }: { scores: Record<string, DemoScore>; instruments: InstrumentKey[] }) {
  return (
    <div className={baseCss.instrumentStatusRow}>
      {instruments.map(key => {
        /* v8 ignore start -- scores[key] always exists; fallback is defensive */
        const sc = scores[key] ?? { hasScore: false, isFC: false };
        /* v8 ignore stop */
        return <InstrumentChip key={key} instrument={key} hasScore={sc.hasScore} isFC={sc.isFC} />;

      })}
    </div>
  );
}

export default function SongIconsDemo() {
  const isMobile = useIsMobileChrome();
  const { settings } = useSettings();
  const instruments = useMemo(() => visibleInstruments(settings), [settings]);
  const { rows, fadingIdx, initialDone } = useDemoSongs({
    rowHeight: ROW_HEIGHT_DESKTOP,
    mobileRowHeight: ROW_HEIGHT_MOBILE,
    isMobile,
  });

  // Memoize scores per title so they stay stable across re-renders.
  const scoresMap = useMemo(() => {
    const map = new Map<string, Record<string, DemoScore>>();
    for (const song of rows) {
      /* v8 ignore start -- titles are unique; dedup guard is defensive */
      if (!map.has(song.title)) map.set(song.title, buildScores(song.title, instruments));
      /* v8 ignore stop */
    }
    return map;
  }, [rows, instruments]);

  return (
    <div className={css.list}>
      {rows.map((song, i) => {
        /* v8 ignore start -- scoresMap is built from the same rows; fallback is defensive */
        const scores = scoresMap.get(song.title) ?? buildScores(song.title, instruments);
        /* v8 ignore stop */
        return (
          <DemoSongRow key={i} index={i} initialDone={initialDone} fadingIdx={fadingIdx} mobile={isMobile}>
            {isMobile ? (
              <>
                <div className={css.mobileTopRow}>
                  <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} />
                </div>
                <div style={{ display: 'flex', justifyContent: 'center' }}>
                  <ChipRow scores={scores} instruments={instruments} />
                </div>
              </>
            ) : (
              <>
                <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} />
                <ChipRow scores={scores} instruments={instruments} />
              </>
            )}
          </DemoSongRow>
        );
      })}
    </div>
  );
}
