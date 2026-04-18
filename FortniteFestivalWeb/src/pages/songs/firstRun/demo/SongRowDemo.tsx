import { type CSSProperties } from 'react';
import SongInfo from '../../../../components/songs/metadata/SongInfo';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { Layout, Gap, CssValue, flexColumn } from '@festival/theme';
import { useDemoSongs } from '../../../../hooks/data/useDemoSongs';
import { DemoSongRow } from './DemoSongRow';
import { mobileTopRow } from '../../../../styles/songRowStyles';

const ROW_HEIGHT_DESKTOP = Layout.demoRowHeight;
const ROW_HEIGHT_MOBILE = Layout.demoRowMobileHeight;

const containerStyle: CSSProperties = { width: CssValue.full, ...flexColumn, gap: Gap.sm };

export default function SongRowDemo() {
  const isMobile = useIsMobile();
  const { rows, fadingIdx, initialDone } = useDemoSongs({
    rowHeight: ROW_HEIGHT_DESKTOP,
    mobileRowHeight: ROW_HEIGHT_MOBILE,
    isMobile,
  });

  return (
    <div style={containerStyle}>
      {rows.map((song, i) => (
        <DemoSongRow key={i} index={i} initialDone={initialDone} fadingIdx={fadingIdx} mobile={isMobile}>
          {isMobile ? (
            <div style={mobileTopRow}>
              <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} minWidth={0} />
            </div>
          ) : (
            <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} />
          )}
        </DemoSongRow>
      ))}
    </div>
  );
}
