/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useMemo, useCallback, type CSSProperties } from 'react';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoAlertCircleOutline, IoWarning } from 'react-icons/io5';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { Colors, InstrumentSize, Cursor, flexCenter } from '@festival/theme';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import anim from '../../../styles/animations.module.css';

const INSTRUMENT_I18N_KEY: Record<string, string> = {
  Solo_Guitar: 'instruments.lead',
  Solo_Bass: 'instruments.bass',
  Solo_Drums: 'instruments.drums',
  Solo_Vocals: 'instruments.vocals',
  Solo_PeripheralGuitar: 'instruments.proLead',
  Solo_PeripheralBass: 'instruments.proBass',
  Solo_PeripheralVocals: 'instruments.peripheralVocals',
  Solo_PeripheralCymbals: 'instruments.peripheralCymbals',
  Solo_PeripheralDrums: 'instruments.peripheralDrums',
};

export default function InvalidScoreIcon({
  songTitle,
  invalidInstruments,
  instrumentFilter,
}: {
  songTitle: string;
  /** Map of instrument → reason for each invalid instrument on this song. */
  invalidInstruments: Map<InstrumentKey, 'fallback' | 'no-fallback' | 'over-threshold'>;
  /** When set, only show info for the filtered instrument; otherwise list all. */
  instrumentFilter?: InstrumentKey | null;
}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [showAlert, setShowAlert] = useState(false);
  // Determine if this is an over-threshold warning (yellow) vs filtered-out error (red)
  const isOverThreshold = useMemo(() => {
    if (instrumentFilter != null) {
      return invalidInstruments.get(instrumentFilter) === 'over-threshold';
    }
    return [...invalidInstruments.values()].every(r => r === 'over-threshold');
  }, [invalidInstruments, instrumentFilter]);

  const s = useStyles(isOverThreshold);

  const handleClick = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setShowAlert(true);
  }, []);

  const handleDismiss = useCallback(() => setShowAlert(false), []);
  const handleGoSettings = useCallback(() => {
    setShowAlert(false);
    navigate('/settings');
  }, [navigate]);

  const alertMessage = useMemo(() => {
    // Determine which instruments to describe
    type Entry = { name: string; reason: 'fallback' | 'no-fallback' | 'over-threshold' };
    const instruments: Entry[] = [];
    if (instrumentFilter != null && invalidInstruments.has(instrumentFilter)) {
      instruments.push({
        name: t(INSTRUMENT_I18N_KEY[instrumentFilter] ?? instrumentFilter),
        reason: invalidInstruments.get(instrumentFilter)!,
      });
    } else {
      for (const [inst, reason] of invalidInstruments) {
        instruments.push({
          name: t(INSTRUMENT_I18N_KEY[inst] ?? inst),
          reason,
        });
      }
    }
    if (instruments.length === 0) return '';

    const names = instruments.map(i => i.name);
    const instNames = names.length <= 2
      ? names.join(' and ')
      : `${names.slice(0, -1).join(', ')}, and ${names[names.length - 1]}`;
    const isChipsView = instrumentFilter == null;

    // All over-threshold: different header + no footer
    if (instruments.every(i => i.reason === 'over-threshold')) {
      return t('songs.invalidScoreOverThreshold', { song: songTitle, instruments: instNames });
    }

    const header = t('songs.invalidScoreHeader', { song: songTitle, instruments: instNames });

    // State-dependent text differs between chips view (no scores shown) and filtered view
    const detailParts: string[] = [];
    const fallbackInsts = instruments.filter(i => i.reason === 'fallback');
    const otherInsts = instruments.filter(i => i.reason !== 'fallback');

    if (fallbackInsts.length > 0) {
      if (!isChipsView) {
        detailParts.push(t('songs.invalidScoreHasFallback'));
      } else if (fallbackInsts.length === 1) {
        detailParts.push(t('songs.invalidScoreHasFallbackChip', { instrument: fallbackInsts[0].name }));
      } else {
        detailParts.push(t('songs.invalidScoreHasFallbackChipPlural'));
      }
    }
    for (const i of otherInsts) {
      if (i.reason === 'over-threshold') detailParts.push(t('songs.invalidScoreOverThresholdDetail', { instrument: i.name }));
      else detailParts.push(t('songs.invalidScoreNoFallback', { instrument: i.name }));
    }
    const details = detailParts.join(' ');

    const footerKey = instruments.length > 1 ? 'songs.invalidScoreFooterPlural' : 'songs.invalidScoreFooter';
    const footer = t(footerKey, { toggle: t('settings.filterInvalidScores') });

    const filterHint = ' ' + (isChipsView
      ? t('songs.invalidScoreFilterHint', { filterName: t('filter.overThreshold') })
      : t('songs.invalidScoreFilterHintSingle', {
          filterName: t('filter.instrumentOverThreshold', { instrument: t(INSTRUMENT_I18N_KEY[instrumentFilter!]) }),
        }));

    return `${header} ${details}\n\n${footer}${filterHint}`;
  }, [invalidInstruments, instrumentFilter, songTitle, t]);

  return (
    <>
      <span
        role="button"
        tabIndex={0}
        aria-label={t('songs.invalidScoreAriaLabel')}
        className={anim.choptIconPulse}
        style={s.container}
        onClick={handleClick}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); handleClick(e as unknown as React.MouseEvent); } }}
      >
        {isOverThreshold
          ? <IoWarning size={InstrumentSize.chip} />
          : <IoAlertCircleOutline size={InstrumentSize.chip} />}
      </span>
      {showAlert && (
        <ConfirmAlert
          title={t('songs.invalidScoreTitle')}
          message={alertMessage}
          onNo={handleDismiss}
          onYes={handleGoSettings}
          onExitComplete={handleDismiss}
          noLabel={t('common.ok')}
          yesLabel={t('songs.goToSettings')}
        />
      )}
    </>
  );
}

function useStyles(isWarning: boolean) {
  return useMemo(() => ({
    container: {
      color: isWarning ? Colors.gold : Colors.statusRed,
      ...flexCenter,
      cursor: Cursor.pointer,
      flexShrink: 0,
      padding: 0,
    } as CSSProperties,
  }), [isWarning]);
}
