/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * First-run demo: song rows with real instrument status chips.
 * Auto-fits rows, rotates one every 5 s with fade-out/in.
 */
import { useMemo, useRef } from 'react';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentChip } from '../../../../components/display/InstrumentChip';
import SongInfo from '../../../../components/songs/metadata/SongInfo';
import { useIsMobileChrome } from '../../../../hooks/ui/useIsMobile';
import { useContainerWidth } from '../../../../hooks/ui/useContainerWidth';
import { useSettings, visibleInstruments } from '../../../../contexts/SettingsContext';
import { Align, CssValue, Display, Gap, Layout, flexColumn } from '@festival/theme';
import { useDemoSongs } from '../../../../hooks/data/useDemoSongs';
import { resolveInstrumentChipRows, splitInstrumentRows } from '../../layoutMode';
import { DemoSongRow } from './DemoSongRow';
import { instrumentStatusRow, mobileTopRow as mobileTopRowStyle } from '../../../../styles/songRowStyles';

type DemoScore = { hasScore: boolean; isFC: boolean };

const ROW_HEIGHT_DESKTOP = Layout.demoRowHeight;
const ROW_HEIGHT_MOBILE = Layout.demoRowMobileIconsHeight;
const containerStyle = { width: CssValue.full, ...flexColumn, gap: Gap.sm };

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

function ChipRow({
  scores,
  instruments,
  rowName,
}: {
  scores: Record<string, DemoScore>;
  instruments: InstrumentKey[];
  rowName?: 'desktop' | 'single' | 'top' | 'bottom';
}) {
  return (
    <div data-instrument-row={rowName} style={instrumentStatusRow}>
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
  const containerRef = useRef<HTMLDivElement>(null);
  const containerWidth = useContainerWidth(containerRef);
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

  const instrumentRowWidth = containerWidth > 0
    ? containerWidth
    : (typeof window !== 'undefined' ? window.innerWidth : undefined);

  const mobileInstrumentRowCount = isMobile
    ? resolveInstrumentChipRows(instrumentRowWidth, instruments.length)
    : 1;
  const [topRowInstruments, bottomRowInstruments] = useMemo(() => {
    if (!isMobile || mobileInstrumentRowCount === 1) return [instruments, []] as const;
    return splitInstrumentRows(instruments);
  }, [instruments, isMobile, mobileInstrumentRowCount]);

  return (
    <div ref={containerRef} style={containerStyle}>
      {rows.map((song, i) => {
        /* v8 ignore start -- scoresMap is built from the same rows; fallback is defensive */
        const scores = scoresMap.get(song.title) ?? buildScores(song.title, instruments);
        /* v8 ignore stop */
        return (
          <DemoSongRow key={i} index={i} initialDone={initialDone} fadingIdx={fadingIdx} mobile={isMobile}>
            {isMobile ? (
              <>
                <div style={mobileTopRowStyle}>
                  <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} minWidth={0} />
                </div>
                <div style={{ display: Display.flex, justifyContent: 'center' }}>
                  {mobileInstrumentRowCount === 1 ? (
                    <ChipRow scores={scores} instruments={topRowInstruments} rowName="single" />
                  ) : (
                    <div style={{ display: Display.flex, flexDirection: 'column', gap: Gap.sm, alignItems: Align.center }}>
                      <ChipRow scores={scores} instruments={topRowInstruments} rowName="top" />
                      <ChipRow scores={scores} instruments={bottomRowInstruments} rowName="bottom" />
                    </div>
                  )}
                </div>
              </>
            ) : (
              <>
                <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} />
                <ChipRow scores={scores} instruments={instruments} rowName="desktop" />
              </>
            )}
          </DemoSongRow>
        );
      })}
    </div>
  );
}
