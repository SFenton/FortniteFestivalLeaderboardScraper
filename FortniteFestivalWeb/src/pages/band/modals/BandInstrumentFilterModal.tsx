/* eslint-disable react/forbid-dom-props -- modal row layout uses inline style objects */
import { useCallback, useEffect, useMemo, useState, type CSSProperties } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  SERVER_INSTRUMENT_KEYS,
  serverInstrumentLabel,
  type BandConfiguration,
  type PlayerBandMember,
  type ServerInstrumentKey,
} from '@festival/core/api/serverTypes';
import { Align, Colors, Font, Gap, Radius, Weight, flexColumn, frostedCard, padding } from '@festival/theme';
import { api } from '../../../api/client';
import { queryKeys } from '../../../api/queryKeys';
import type { SelectedBandProfile } from '../../../hooks/data/useSelectedProfile';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import { useModalDraft } from '../../../hooks/ui/useModalDraft';
import { InstrumentSelector, type InstrumentSelectorItem } from '../../../components/common/InstrumentSelector';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import Modal from '../../../components/modals/Modal';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import type { BandInstrumentFilterApplyPayload, BandInstrumentFilterAssignment } from '../../../types/bandFilter';

export type { BandInstrumentFilterApplyPayload, BandInstrumentFilterAssignment } from '../../../types/bandFilter';

type BandInstrumentFilterModalProps = {
  visible: boolean;
  selectedBand: SelectedBandProfile | null;
  appliedAssignments: readonly BandInstrumentFilterAssignment[];
  onCancel: () => void;
  onApply: (payload: BandInstrumentFilterApplyPayload) => void;
  onReset: () => void;
};

type PendingInvalidSelection = {
  index: number;
  instrument: ServerInstrumentKey;
} | null;

const INSTRUMENT_ITEMS: InstrumentSelectorItem<ServerInstrumentKey>[] = SERVER_INSTRUMENT_KEYS.map(key => ({
  key,
  label: serverInstrumentLabel(key),
}));
const BAND_FILTER_DETAIL_STALE_MS = 300_000;
const EMPTY_CONFIGURATIONS: BandConfiguration[] = [];

export default function BandInstrumentFilterModal({
  visible,
  selectedBand,
  appliedAssignments,
  onCancel,
  onApply,
  onReset,
}: BandInstrumentFilterModalProps) {
  const { t } = useTranslation();
  const isMobile = useIsMobile();
  const styles = useStyles();
  const [draft, setDraft] = useState<(ServerInstrumentKey | null)[]>([]);
  const [pendingInvalidSelection, setPendingInvalidSelection] = useState<PendingInvalidSelection>(null);
  const bandId = selectedBand?.bandId ?? '';

  const detailQuery = useQuery({
    queryKey: queryKeys.bandDetail(bandId),
    queryFn: () => api.getBandDetail(bandId),
    enabled: visible && !!bandId,
    staleTime: BAND_FILTER_DETAIL_STALE_MS,
  });

  const members = useMemo<PlayerBandMember[]>(() => {
    const hydratedMembers = detailQuery.data?.band.members;
    if (hydratedMembers?.length) return hydratedMembers;
    return selectedBand?.members.map(member => ({ ...member, instruments: [] })) ?? [];
  }, [detailQuery.data?.band.members, selectedBand?.members]);

  const savedDraft = useMemo(
    () => members.map(member => appliedAssignments.find(assignment => assignment.accountId === member.accountId)?.instrument ?? null),
    [appliedAssignments, members],
  );

  const configurations = detailQuery.data?.configurations ?? EMPTY_CONFIGURATIONS;
  const hasConfigurationData = configurations.length > 0;
  const availableInstruments = useMemo(
    () => getAvailableInstruments(configurations),
    [configurations],
  );
  const instrumentStates = useMemo(
    () => getInstrumentStates(configurations, draft, hasConfigurationData, availableInstruments),
    [availableInstruments, configurations, draft, hasConfigurationData],
  );
  const complete = draft.length === members.length && draft.length > 0 && draft.every(Boolean);
  const matchingConfiguration = complete ? findMatchingConfiguration(configurations, draft) : null;
  const applyDisabled = !complete || !matchingConfiguration;
  const { hasChanges, confirmOpen, setConfirmOpen, handleClose } = useModalDraft(
    draft,
    savedDraft,
    onCancel,
    areDraftsEqual,
  );

  useEffect(() => {
    if (!visible) return;
    setDraft(savedDraft);
    setPendingInvalidSelection(null);
  }, [savedDraft, visible]);

  const handleSelect = useCallback((index: number, instrument: ServerInstrumentKey | null) => {
    setPendingInvalidSelection(null);
    setConfirmOpen(false);

    if (!instrument || !hasConfigurationData) {
      setDraft(current => replaceAt(current, index, instrument));
      return;
    }

    const nextDraft = replaceAt(draft, index, instrument);
    const selectedCount = nextDraft.filter(Boolean).length;
    if (!hasPartialMatchingConfiguration(configurations, nextDraft)) {
      if (selectedCount <= 1) return;
      setPendingInvalidSelection({ index, instrument });
      return;
    }

    setDraft(nextDraft);
  }, [configurations, draft, hasConfigurationData, setConfirmOpen]);

  const confirmInvalidSelection = useCallback(() => {
    if (!pendingInvalidSelection) return;
    setDraft(members.map((_, index) => index === pendingInvalidSelection.index ? pendingInvalidSelection.instrument : null));
    setPendingInvalidSelection(null);
  }, [members, pendingInvalidSelection]);

  const resetDraft = useCallback(() => {
    setDraft(members.map(() => null));
    if (appliedAssignments.length > 0) onReset();
  }, [appliedAssignments.length, members, onReset]);

  const apply = useCallback(() => {
    if (applyDisabled || !matchingConfiguration) return;
    onApply({
      comboId: matchingConfiguration.comboId,
      assignments: members.map((member, index) => ({
        accountId: member.accountId,
        instrument: draft[index]!,
      })),
    });
  }, [applyDisabled, draft, matchingConfiguration, members, onApply]);

  return (
    <Modal
      visible={visible}
      title={t('bandFilter.modalTitle')}
      onClose={handleClose}
      onApply={apply}
      onReset={resetDraft}
      resetLabel={t('bandFilter.resetTitle')}
      applyDisabled={applyDisabled || !hasChanges}
      afterPanel={(
        pendingInvalidSelection ? (
          <ConfirmAlert
            title={t('bandFilter.invalidTitle')}
            message={t('bandFilter.invalidMessage')}
            noLabel={t('common.cancel')}
            yesLabel={t('bandFilter.keepSelection')}
            onNo={() => setPendingInvalidSelection(null)}
            onYes={confirmInvalidSelection}
            onExitComplete={() => setPendingInvalidSelection(null)}
          />
        ) : confirmOpen ? (
          <ConfirmAlert
            title={t('bandFilter.cancelTitle')}
            message={t('bandFilter.cancelMessage')}
            onNo={() => setConfirmOpen(false)}
            onYes={onCancel}
            onExitComplete={() => setConfirmOpen(false)}
          />
        ) : null
      )}
    >
      <ModalSection>
        <div style={styles.rows}>
          {detailQuery.isLoading && members.length === 0 ? (
            <div style={styles.status}>{t('common.loading')}</div>
          ) : detailQuery.error ? (
            <div style={styles.status}>{t('bandFilter.loadFailed')}</div>
          ) : members.map((member, index) => (
            <BandmateInstrumentRow
              key={member.accountId}
              index={index}
              member={member}
              selected={draft[index] ?? null}
              onSelect={(instrument) => handleSelect(index, instrument)}
              availableInstruments={availableInstruments}
              disabledInstruments={instrumentStates[index]?.disabled ?? []}
              mutedInstruments={instrumentStates[index]?.muted ?? []}
              compact={isMobile}
              styles={styles}
            />
          ))}
        </div>
      </ModalSection>
    </Modal>
  );
}

function BandmateInstrumentRow({
  index,
  member,
  selected,
  onSelect,
  availableInstruments,
  disabledInstruments,
  mutedInstruments,
  compact,
  styles,
}: {
  index: number;
  member: PlayerBandMember;
  selected: ServerInstrumentKey | null;
  onSelect: (instrument: ServerInstrumentKey | null) => void;
  availableInstruments: readonly ServerInstrumentKey[];
  disabledInstruments: readonly ServerInstrumentKey[];
  mutedInstruments: readonly ServerInstrumentKey[];
  compact: boolean;
  styles: ReturnType<typeof useStyles>;
}) {
  const { t } = useTranslation();
  const allowed = new Set(availableInstruments);
  const hiddenInstruments = SERVER_INSTRUMENT_KEYS.filter(instrument => !allowed.has(instrument));
  const name = member.displayName?.trim() || t('common.unknownUser');

  return (
    <div style={styles.row}>
      <div style={styles.rowHeader}>
        <span style={styles.bandmateLabel}>{t('bandFilter.bandmateLabel', { index: index + 1 })}</span>
        <span style={styles.bandmateName}>{name}</span>
      </div>
      {availableInstruments.length > 0 ? (
        <InstrumentSelector
          instruments={INSTRUMENT_ITEMS}
          selected={selected}
          onSelect={onSelect}
          hiddenInstruments={hiddenInstruments}
          disabledInstruments={disabledInstruments}
          mutedInstruments={mutedInstruments.filter(instrument => instrument !== selected)}
          compact={compact}
          deferSelection={compact}
          compactLabels={{ previous: t('aria.previousInstrument'), next: t('aria.nextInstrument') }}
        />
      ) : (
        <div style={styles.status}>{t('bandFilter.noInstruments')}</div>
      )}
    </div>
  );
}

function replaceAt<T>(items: readonly T[], index: number, value: T): T[] {
  return items.map((item, itemIndex) => itemIndex === index ? value : item);
}

function areDraftsEqual(a: readonly (ServerInstrumentKey | null)[], b: readonly (ServerInstrumentKey | null)[]) {
  return a.length === b.length && a.every((value, index) => value === b[index]);
}

function hasPartialMatchingConfiguration(
  configurations: readonly BandConfiguration[],
  draft: readonly (ServerInstrumentKey | null)[],
) {
  const selected = selectedInstrumentsFromDraft(draft);
  return configurations.some(configuration => containsInstrumentMultiset(configuration.instruments, selected));
}

function findMatchingConfiguration(
  configurations: readonly BandConfiguration[],
  draft: readonly (ServerInstrumentKey | null)[],
) {
  const selected = selectedInstrumentsFromDraft(draft);
  return configurations.find(configuration => areInstrumentMultisetsEqual(configuration.instruments, selected)) ?? null;
}

function getInstrumentStates(
  configurations: readonly BandConfiguration[],
  draft: readonly (ServerInstrumentKey | null)[],
  hasConfigurationData: boolean,
  availableInstruments: readonly ServerInstrumentKey[],
) {
  return draft.map((_, index) => {
    if (!hasConfigurationData) return { disabled: [], muted: [] };

    const possibleInstrumentSet = new Set(availableInstruments);

    const disabled: ServerInstrumentKey[] = [];
    const muted: ServerInstrumentKey[] = [];
    for (const instrument of SERVER_INSTRUMENT_KEYS) {
      if (!possibleInstrumentSet.has(instrument)) {
        disabled.push(instrument);
      } else if (!canSelectInstrument(configurations, draft, index, instrument)) {
        muted.push(instrument);
      }
    }

    return { disabled, muted };
  });
}

function canSelectInstrument(
  configurations: readonly BandConfiguration[],
  draft: readonly (ServerInstrumentKey | null)[],
  index: number,
  instrument: ServerInstrumentKey,
) {
  return hasPartialMatchingConfiguration(configurations, replaceAt(draft, index, instrument));
}

function getAvailableInstruments(configurations: readonly BandConfiguration[]): ServerInstrumentKey[] {
  const available = new Set(configurations.flatMap(configuration => configuration.instruments));
  return SERVER_INSTRUMENT_KEYS.filter(instrument => available.has(instrument));
}

function selectedInstrumentsFromDraft(draft: readonly (ServerInstrumentKey | null)[]): ServerInstrumentKey[] {
  return draft.filter((instrument): instrument is ServerInstrumentKey => !!instrument);
}

function containsInstrumentMultiset(
  available: readonly ServerInstrumentKey[],
  selected: readonly ServerInstrumentKey[],
): boolean {
  const counts = toInstrumentCounts(available);
  return selected.every(instrument => {
    const remaining = counts.get(instrument) ?? 0;
    if (remaining <= 0) return false;
    counts.set(instrument, remaining - 1);
    return true;
  });
}

function areInstrumentMultisetsEqual(
  a: readonly ServerInstrumentKey[],
  b: readonly ServerInstrumentKey[],
): boolean {
  if (a.length !== b.length) return false;
  return containsInstrumentMultiset(a, b);
}

function toInstrumentCounts(instruments: readonly ServerInstrumentKey[]) {
  const counts = new Map<ServerInstrumentKey, number>();
  for (const instrument of instruments) {
    counts.set(instrument, (counts.get(instrument) ?? 0) + 1);
  }
  return counts;
}

function useStyles() {
  return useMemo(() => ({
    rows: {
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
    row: {
      ...frostedCard,
      ...flexColumn,
      gap: Gap.md,
      borderRadius: Radius.md,
      padding: padding(Gap.lg),
    } as CSSProperties,
    rowHeader: {
      display: 'flex',
      alignItems: Align.baseline,
      justifyContent: 'space-between',
      gap: Gap.md,
      minWidth: 0,
    } as CSSProperties,
    bandmateLabel: {
      color: Colors.textSecondary,
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      whiteSpace: 'nowrap',
    } as CSSProperties,
    bandmateName: {
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontWeight: Weight.bold,
      minWidth: 0,
      overflow: 'hidden',
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
    } as CSSProperties,
    status: {
      color: Colors.textSecondary,
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
    } as CSSProperties,
  }), []);
}
