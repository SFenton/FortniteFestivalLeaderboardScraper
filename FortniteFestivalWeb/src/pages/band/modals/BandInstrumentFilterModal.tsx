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

export type BandInstrumentFilterAssignment = {
  accountId: string;
  instrument: ServerInstrumentKey;
};

type BandInstrumentFilterModalProps = {
  visible: boolean;
  selectedBand: SelectedBandProfile | null;
  appliedAssignments: readonly BandInstrumentFilterAssignment[];
  onCancel: () => void;
  onApply: (assignments: BandInstrumentFilterAssignment[]) => void;
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
  const instrumentStates = useMemo(
    () => getInstrumentStates(configurations, members, draft, hasConfigurationData),
    [configurations, draft, hasConfigurationData, members],
  );
  const complete = draft.length === members.length && draft.length > 0 && draft.every(Boolean);
  const matchingConfiguration = complete ? findMatchingConfiguration(configurations, members, draft) : null;
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
    if (!hasPartialMatchingConfiguration(configurations, members, nextDraft)) {
      if (selectedCount <= 1) return;
      setPendingInvalidSelection({ index, instrument });
      return;
    }

    setDraft(nextDraft);
  }, [configurations, draft, hasConfigurationData, members, setConfirmOpen]);

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
    if (applyDisabled) return;
    onApply(members.map((member, index) => ({
      accountId: member.accountId,
      instrument: draft[index]!,
    })));
  }, [applyDisabled, draft, members, onApply]);

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
  disabledInstruments,
  mutedInstruments,
  compact,
  styles,
}: {
  index: number;
  member: PlayerBandMember;
  selected: ServerInstrumentKey | null;
  onSelect: (instrument: ServerInstrumentKey | null) => void;
  disabledInstruments: readonly ServerInstrumentKey[];
  mutedInstruments: readonly ServerInstrumentKey[];
  compact: boolean;
  styles: ReturnType<typeof useStyles>;
}) {
  const { t } = useTranslation();
  const allowed = new Set(member.instruments);
  const hiddenInstruments = SERVER_INSTRUMENT_KEYS.filter(instrument => !allowed.has(instrument));
  const name = member.displayName?.trim() || t('common.unknownUser');

  return (
    <div style={styles.row}>
      <div style={styles.rowHeader}>
        <span style={styles.bandmateLabel}>{t('bandFilter.bandmateLabel', { index: index + 1 })}</span>
        <span style={styles.bandmateName}>{name}</span>
      </div>
      {member.instruments.length > 0 ? (
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
  members: readonly PlayerBandMember[],
  draft: readonly (ServerInstrumentKey | null)[],
) {
  return configurations.some(configuration => members.every((member, index) => {
    const selected = draft[index];
    return !selected || configuration.memberInstruments[member.accountId] === selected;
  }));
}

function findMatchingConfiguration(
  configurations: readonly BandConfiguration[],
  members: readonly PlayerBandMember[],
  draft: readonly (ServerInstrumentKey | null)[],
) {
  return configurations.find(configuration => members.every((member, index) => (
    configuration.memberInstruments[member.accountId] === draft[index]
  ))) ?? null;
}

function getInstrumentStates(
  configurations: readonly BandConfiguration[],
  members: readonly PlayerBandMember[],
  draft: readonly (ServerInstrumentKey | null)[],
  hasConfigurationData: boolean,
) {
  return members.map((member, index) => {
    if (!hasConfigurationData) return { disabled: [], muted: [] };

    const memberInstrumentSet = new Set(member.instruments);
    const possibleInstrumentSet = new Set(configurations
      .map(configuration => configuration.memberInstruments[member.accountId])
      .filter((instrument): instrument is ServerInstrumentKey => !!instrument));

    const disabled: ServerInstrumentKey[] = [];
    const muted: ServerInstrumentKey[] = [];
    for (const instrument of SERVER_INSTRUMENT_KEYS) {
      if (!memberInstrumentSet.has(instrument)) continue;
      if (!possibleInstrumentSet.has(instrument)) {
        disabled.push(instrument);
      } else if (!canSelectInstrument(configurations, members, draft, index, instrument)) {
        muted.push(instrument);
      }
    }

    return { disabled, muted };
  });
}

function canSelectInstrument(
  configurations: readonly BandConfiguration[],
  members: readonly PlayerBandMember[],
  draft: readonly (ServerInstrumentKey | null)[],
  index: number,
  instrument: ServerInstrumentKey,
) {
  return hasPartialMatchingConfiguration(configurations, members, replaceAt(draft, index, instrument));
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
