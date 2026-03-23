/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo } from 'react';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { Font, Gap, Size } from '@festival/theme';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import s from './PlayerSectionHeading.module.css';

/** Reusable section heading for the player page (title + optional description).
 *  When `instrument` is provided, renders as a compact row with an instrument icon. */
const PlayerSectionHeading = memo(function PlayerSectionHeading({
  title,
  description,
  instrument,
  compact,
}: {
  title: string;
  description?: string;
  /** Show an instrument icon to the left. */
  instrument?: InstrumentKey;
  /** Use tighter top margin (Gap.md instead of Gap.section). */
  compact?: boolean;
}) {
  if (instrument) {
    return (
      <div className={s.instCardHeader} style={compact ? { marginTop: Gap.md } : undefined}>
        <InstrumentIcon instrument={instrument} size={Size.iconInstrument} />
        <div style={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: Size.iconInstrument }}>
          <span className={s.instCardTitle}>{title}</span>
          {description && <span className={s.sectionDesc} style={{ margin: 0, fontSize: Font.md }}>{description}</span>}
        </div>
      </div>
    );
  }

  return (
    <div style={{ marginTop: Gap.section }}>
      <h2 className={s.sectionTitle}>{title}</h2>
      {description && <p className={s.sectionDesc}>{description}</p>}
    </div>
  );
});

export default PlayerSectionHeading;
