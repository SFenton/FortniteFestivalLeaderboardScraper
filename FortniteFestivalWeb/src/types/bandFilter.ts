import type { PlayerBandType, ServerInstrumentKey } from '@festival/core/api/serverTypes';

export type BandInstrumentFilterAssignment = {
  accountId: string;
  instrument: ServerInstrumentKey;
};

export type BandInstrumentFilterApplyPayload = {
  comboId: string;
  assignments: BandInstrumentFilterAssignment[];
};

export type AppliedBandComboFilter = {
  bandId: string;
  bandType: PlayerBandType;
  teamKey: string;
  comboId: string;
  assignments: BandInstrumentFilterAssignment[];
};
