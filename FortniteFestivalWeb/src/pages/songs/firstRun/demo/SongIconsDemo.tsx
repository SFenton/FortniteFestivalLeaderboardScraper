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
import { Align, CssValue, Display, Gap, Layout, Size, flexColumn } from '@festival/theme';
import { useDemoSongs } from '../../../../hooks/data/useDemoSongs';
import { resolveInstrumentChipRows, splitInstrumentRows, type InstrumentChipRowCount } from '../../layoutMode';
import { DemoSongRow } from './DemoSongRow';
import { instrumentStatusRow, mobileTopRow as mobileTopRowStyle } from '../../../../styles/songRowStyles';

type DemoScore = { hasScore: boolean; isFC: boolean };

const ROW_HEIGHT_DESKTOP = Layout.demoRowHeight;
const ROW_HEIGHT_MOBILE = Layout.demoRowMobileIconsHeight;
const ROW_HEIGHT_THREE_ROWS = Layout.demoRowMobileIconsHeight + 44;
const DESKTOP_ICON_ROW_RESERVED_WIDTH = Size.thumb + 200 + Gap.xl * 2;
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
  rowName?: 'desktop' | 'single' | 'top' | 'middle' | 'bottom';
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
  const instrumentRowCountRef = useRef<InstrumentChipRowCount>(1);
  const containerWidth = useContainerWidth(containerRef);
  const instruments = useMemo(() => visibleInstruments(settings), [settings]);
  const instrumentRowWidth = containerWidth > 0
    ? containerWidth
    : (typeof window !== 'undefined' ? window.innerWidth : undefined);
  const desktopInstrumentRowWidth = instrumentRowWidth == null
    ? undefined
    : Math.max(1, instrumentRowWidth - DESKTOP_ICON_ROW_RESERVED_WIDTH);
  const desktopRowCount = resolveInstrumentChipRows(
    desktopInstrumentRowWidth,
    instruments.length,
    undefined,
    undefined,
    undefined,
    instrumentRowCountRef.current,
  );
  const stackedInstrumentRowCount = resolveInstrumentChipRows(
    instrumentRowWidth,
    instruments.length,
    undefined,
    undefined,
    undefined,
    instrumentRowCountRef.current,
  );
  const useStackedLayout = isMobile || desktopRowCount > 1;
  instrumentRowCountRef.current = stackedInstrumentRowCount;
  const compactRowHeight = stackedInstrumentRowCount >= 3 ? ROW_HEIGHT_THREE_ROWS : ROW_HEIGHT_MOBILE;
  const { rows, fadingIdx, initialDone } = useDemoSongs({
    rowHeight: ROW_HEIGHT_DESKTOP,
    mobileRowHeight: compactRowHeight,
    isMobile: useStackedLayout,
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

  const instrumentRows = useMemo(() => {
    if (!useStackedLayout || stackedInstrumentRowCount === 1) return [instruments];
    return splitInstrumentRows(instruments, stackedInstrumentRowCount);
  }, [instruments, stackedInstrumentRowCount, useStackedLayout]);

  return (
    <div ref={containerRef} style={containerStyle}>
      {rows.map((song, i) => {
        /* v8 ignore start -- scoresMap is built from the same rows; fallback is defensive */
        const scores = scoresMap.get(song.title) ?? buildScores(song.title, instruments);
        /* v8 ignore stop */
        return (
          <DemoSongRow key={i} index={i} initialDone={initialDone} fadingIdx={fadingIdx} mobile={useStackedLayout}>
            {useStackedLayout ? (
              <>
                <div style={mobileTopRowStyle}>
                  <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} minWidth={0} />
                </div>
                <div style={{ display: Display.flex, justifyContent: 'center' }}>
                  {instrumentRows.length === 1 ? (
                    <ChipRow scores={scores} instruments={instrumentRows[0]!} rowName="single" />
                  ) : (
                    <div style={{ display: Display.flex, flexDirection: 'column', gap: Gap.sm, alignItems: Align.center }}>
                      {instrumentRows.map((row, rowIndex) => {
                        const rowName = rowIndex === 0
                          ? 'top'
                          : rowIndex === instrumentRows.length - 1
                            ? 'bottom'
                            : 'middle';
                        return <ChipRow key={rowName} scores={scores} instruments={row} rowName={rowName} />;
                      })}
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
