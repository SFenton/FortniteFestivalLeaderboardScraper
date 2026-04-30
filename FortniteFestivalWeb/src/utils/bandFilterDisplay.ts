import { serverInstrumentLabel, type ServerInstrumentKey } from '@festival/core/api/serverTypes';

export function getBandFilterActionLabel(selectedInstruments: readonly ServerInstrumentKey[], emptyLabel: string): string {
  if (selectedInstruments.length === 0) return emptyLabel;
  return selectedInstruments.map(serverInstrumentLabel).join(' / ');
}
