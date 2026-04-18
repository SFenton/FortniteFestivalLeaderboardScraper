/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { InstrumentHeaderSize } from '@festival/core';
import type { ServerSong, ServerInstrumentKey, SongDifficulty } from '@festival/core/api/serverTypes';
import { Gap, Radius, frostedCard } from '@festival/theme';
import InstrumentHeader from '../../../components/display/InstrumentHeader';
import DifficultyBars from '../../../components/songs/metadata/DifficultyBars';
import SectionHeader from '../../../components/common/SectionHeader';

type Row = {
  key: ServerInstrumentKey;
  /** SongDifficulty field used to look up the intensity. Omit when no data exists for this slot. */
  diffField?: keyof SongDifficulty;
};

/**
 * Fixed ordering — matches the instrument list the user requested.
 * Note: Mic Mode, Pro Drums, and Pro Drums + Cymbals have no corresponding
 * field in `SongDifficulty` today, so their bars always render empty.
 */
const ROWS: readonly Row[] = [
  { key: 'Solo_Guitar',           diffField: 'guitar' },
  { key: 'Solo_Bass',             diffField: 'bass' },
  { key: 'Solo_Drums',            diffField: 'drums' },
  { key: 'Solo_Vocals',           diffField: 'vocals' },
  { key: 'Solo_PeripheralGuitar', diffField: 'proGuitar' },
  { key: 'Solo_PeripheralBass',   diffField: 'proBass' },
  { key: 'Solo_PeripheralVocals' },
  { key: 'Solo_PeripheralDrums' },
  { key: 'Solo_PeripheralCymbals' },
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

  const diff = song?.difficulty;

  return (
    <div style={style} onAnimationEnd={onAnimationEnd}>
      <SectionHeader title={t('songInfo.intensity.title', 'Intensity')} />
      <div style={st.card}>
        <div style={st.grid}>
          {ROWS.map((row) => {
            const raw = row.diffField != null ? diff?.[row.diffField] : undefined;
            return (
              <div key={row.key} style={st.row}>
                <InstrumentHeader instrument={row.key} size={InstrumentHeaderSize.SM} iconOnly sig={sig} />
                <DifficultyBars level={raw ?? 0} raw />
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
