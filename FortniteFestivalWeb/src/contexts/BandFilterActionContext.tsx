import { createContext, useContext, type ReactNode } from 'react';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';
import type { AppliedBandComboFilter } from '../types/bandFilter';

export type BandFilterActionContextValue = {
  visible: boolean;
  label: string;
  selectedInstruments: readonly ServerInstrumentKey[];
  appliedFilter?: AppliedBandComboFilter | null;
  onPress: () => void;
};

const noop = () => {};

const DEFAULT_VALUE: BandFilterActionContextValue = {
  visible: false,
  label: 'Filter Band Type',
  selectedInstruments: [],
  appliedFilter: null,
  onPress: noop,
};

const BandFilterActionContext = createContext<BandFilterActionContextValue>(DEFAULT_VALUE);

export function BandFilterActionProvider({ children, value }: { children: ReactNode; value: BandFilterActionContextValue }) {
  return (
    <BandFilterActionContext.Provider value={value}>
      {children}
    </BandFilterActionContext.Provider>
  );
}

export function useBandFilterAction() {
  return useContext(BandFilterActionContext);
}

export function useAppliedBandComboFilter() {
  return useBandFilterAction().appliedFilter ?? null;
}
