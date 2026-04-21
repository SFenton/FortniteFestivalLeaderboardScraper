/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { InstrumentHeaderSize } from '@festival/core';
import type { ServerSong, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { Gap, Radius, frostedCard } from '@festival/theme';
import InstrumentHeader from '../../../components/display/InstrumentHeader';
import DifficultyBars from '../../../components/songs/metadata/DifficultyBars';
import SectionHeader from '../../../components/common/SectionHeader';
import { getSongInstrumentDifficulty } from '../../../utils/songInstrumentDifficulty';

/**
 * Fixed ordering — matches the instrument list the user requested.
 */
const ROWS: readonly ServerInstrumentKey[] = [
  'Solo_Guitar',
  'Solo_Bass',
  'Solo_Drums',
  'Solo_Vocals',
  'Solo_PeripheralGuitar',
  'Solo_PeripheralBass',
  'Solo_PeripheralVocals',
  'Solo_PeripheralDrums',
  'Solo_PeripheralCymbals',
];

interface IntensityCardProps {
  song: ServerSong | undefined;
  sig?: string;
  style?: CSSProperties;
  onAnimationEnd?: (ev: React.AnimationEvent<HTMLElement>) => void;
}

/**
 * "Intensity" glass card for the Song Details page. Renders all 9 instrument
 * slots regardless of the user's enabled-instruments setting, showing the
 * instrument icon, label, and the 7-bar difficulty display used in song rows.
 */
const IntensityCard = memo(function IntensityCard({ song, sig, style, onAnimationEnd }: IntensityCardProps) {
  const { t } = useTranslation();
  const st = useIntensityCardStyles();

  const visibleRows = useMemo(() => ROWS
    .map((key) => ({ key, raw: song ? getSongInstrumentDifficulty(song, key) : undefined }))
    .filter((row) => row.raw != null), [song]);

  if (visibleRows.length === 0) return null;

  return (
    <div style={style} onAnimationEnd={onAnimationEnd}>
      <SectionHeader title={t('songInfo.intensity.title', 'Intensity')} />
      <div style={st.card}>
        <div style={st.grid}>
          {visibleRows.map((row) => {
            return (
              <div key={row.key} style={st.row}>
                <InstrumentHeader instrument={row.key} size={InstrumentHeaderSize.SM} iconOnly sig={sig} />
                <DifficultyBars level={row.raw!} raw />
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
});

function useIntensityCardStyles() {
  return useMemo(() => ({
    card: {
      ...frostedCard,
      padding: `${Gap.xl}px ${Gap.section}px`,
      borderRadius: Radius.md,
      marginTop: Gap.md,
    } as CSSProperties,
    grid: {
      display: 'grid',
      // Icon (36) + gap (8) + DifficultyBars (7*8 + 6 = 62) = 106px per cell.
      // auto-fit packs as many columns as will fit the container.
      gridTemplateColumns: 'repeat(auto-fit, minmax(106px, 1fr))',
      columnGap: Gap.xl,
      rowGap: Gap.sm,
    } as CSSProperties,
    row: {
      display: 'flex',
      alignItems: 'center',
      gap: Gap.md,
    } as CSSProperties,
  }), []);
}

export default IntensityCard;
