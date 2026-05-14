/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useMemo, useCallback } from 'react';
import Modal from '../../../components/modals/Modal';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import { ToggleRow } from '../../../components/common/ToggleRow';
import { Accordion } from '../../../components/common/Accordion';
import { BulkActions } from '../../../components/modals/components/BulkActions';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import { InstrumentSelector, type InstrumentSelectorItem } from '../../../components/common/InstrumentSelector';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { useModalDraft } from '../../../hooks/ui/useModalDraft';
import { INSTRUMENT_KEYS, INSTRUMENT_LABELS } from '@festival/core/api/serverTypes';
import type { SongFilters } from '../../../utils/songSettings';
import type { SelectedBandProfile } from '../../../hooks/data/useSelectedProfile';
import type { BandInstrumentFilterApplyPayload, BandInstrumentFilterAssignment } from '../../../types/bandFilter';
import {
  areBandInstrumentDraftsEqual,
  BandInstrumentFilterInvalidSelectionAlert,
  BandInstrumentFilterPicker,
  useBandInstrumentFilterController,
} from '../../band/modals/BandInstrumentFilterPicker';
import { useSettings, isInstrumentVisible } from '../../../contexts/SettingsContext';
import DifficultyBars from '../../../components/songs/metadata/DifficultyBars';
import { useShopState } from '../../../hooks/data/useShopState';
import { useTranslation } from 'react-i18next';

export type FilterDraft = SongFilters & {
  instrumentFilter: InstrumentKey | null;
};

type BandComboFilterProps = {
  selectedBand: SelectedBandProfile;
  appliedAssignments: readonly BandInstrumentFilterAssignment[];
  onApply: (payload: BandInstrumentFilterApplyPayload) => void;
  onReset: () => void;
};

type FilterModalDraftState = {
  filters: FilterDraft;
  bandCombo: readonly (InstrumentKey | null)[];
};

type FilterModalProps = {
  visible: boolean;
  draft: FilterDraft;
  savedDraft?: FilterDraft;
  availableSeasons: number[];
  selectedBandMode?: boolean;
  selectedBandName?: string;
  selectedBandMembers?: readonly { accountId: string; displayName: string }[];
  bandComboInstruments?: readonly InstrumentKey[];
  bandComboFilter?: BandComboFilterProps;
  onChange: (d: FilterDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

const PERCENTILE_THRESHOLDS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100] as const;
const noopBandApply = () => {};
const noopBandReset = () => {};

export default function FilterModal({ visible, draft, savedDraft, availableSeasons, selectedBandMode = false, selectedBandName, selectedBandMembers = [], bandComboInstruments = [], bandComboFilter, onChange, onCancel, onReset, onApply }: FilterModalProps) {
  const { t } = useTranslation();
  const { settings: appSettings } = useSettings();
  const { isShopVisible } = useShopState();
  const visibleKeys = INSTRUMENT_KEYS.filter(k => isInstrumentVisible(appSettings, k));
  const showBandComboSection = selectedBandMode && !!bandComboFilter;
  const bandComboController = useBandInstrumentFilterController({
    visible: visible && showBandComboSection,
    selectedBand: bandComboFilter?.selectedBand ?? null,
    appliedAssignments: bandComboFilter?.appliedAssignments ?? [],
    onApply: bandComboFilter?.onApply ?? noopBandApply,
    onReset: bandComboFilter?.onReset ?? noopBandReset,
  });

  const toggleMissingScores = (key: InstrumentKey) => {
    onChange({ ...draft, missingScores: { ...draft.missingScores, [key]: !(draft.missingScores[key] ?? false) } });
  };
  const toggleMissingFCs = (key: InstrumentKey) => {
    onChange({ ...draft, missingFCs: { ...draft.missingFCs, [key]: !(draft.missingFCs[key] ?? false) } });
  };
  const toggleHasScores = (key: InstrumentKey) => {
    onChange({ ...draft, hasScores: { ...draft.hasScores, [key]: !(draft.hasScores[key] ?? false) } });
  };
  const toggleHasFCs = (key: InstrumentKey) => {
    onChange({ ...draft, hasFCs: { ...draft.hasFCs, [key]: !(draft.hasFCs[key] ?? false) } });
  };
  const toggleOverThreshold = (key: InstrumentKey) => {
    onChange({ ...draft, overThreshold: { ...draft.overThreshold, [key]: !(draft.overThreshold?.[key] ?? false) } });
  };
  const toggleSelectedBandHasScore = () => {
    const next = !draft.selectedBandHasScore;
    onChange({ ...draft, selectedBandHasScore: next, selectedBandMissingScore: next ? false : draft.selectedBandMissingScore });
  };
  const toggleSelectedBandMissingScore = () => {
    const next = !draft.selectedBandMissingScore;
    onChange({ ...draft, selectedBandMissingScore: next, selectedBandHasScore: next ? false : draft.selectedBandHasScore });
  };
  const toggleIndividualBandMemberHasScore = (accountId: string) => {
    const current = draft.individualBandMemberScoreFilters[accountId] ?? {};
    const next = !(current.hasScore ?? false);
    onChange({
      ...draft,
      individualBandMemberScoreFilters: {
        ...draft.individualBandMemberScoreFilters,
        [accountId]: { hasScore: next, missingScore: next ? false : current.missingScore ?? false },
      },
    });
  };
  const toggleIndividualBandMemberMissingScore = (accountId: string) => {
    const current = draft.individualBandMemberScoreFilters[accountId] ?? {};
    const next = !(current.missingScore ?? false);
    onChange({
      ...draft,
      individualBandMemberScoreFilters: {
        ...draft.individualBandMemberScoreFilters,
        [accountId]: { missingScore: next, hasScore: next ? false : current.hasScore ?? false },
      },
    });
  };

  // Global toggles: set/unset all visible instruments at once
  const allOn = (map: Record<string, boolean>) => visibleKeys.every(k => map[k] === true);
  const toggleGlobal = (field: 'missingScores' | 'missingFCs' | 'hasScores' | 'hasFCs' | 'overThreshold') => {
    const src = field === 'overThreshold' ? (draft[field] ?? {}) : draft[field];
    const current = visibleKeys.every(k => src[k] === true);
    const updated: Record<string, boolean> = { ...src };
    for (const k of visibleKeys) updated[k] = !current;
    onChange({ ...draft, [field]: updated });
  };

  const selectorItems = useMemo<InstrumentSelectorItem[]>(
    () => visibleKeys.map(key => ({ key, label: INSTRUMENT_LABELS[key] })),
    [visibleKeys],
  );

  const handleInstrumentSelect = useCallback((key: InstrumentKey | null) => {
    onChange({ ...draft, instrumentFilter: key });
  }, [draft, onChange]);

  const showIndividualBandMemberFilters = selectedBandMode && selectedBandMembers.length > 0 && bandComboInstruments.length > 0;

  const modalDraft = useMemo<FilterModalDraftState>(() => ({
    filters: draft,
    bandCombo: showBandComboSection ? bandComboController.draft : [],
  }), [bandComboController.draft, draft, showBandComboSection]);
  const modalSavedDraft = useMemo<FilterModalDraftState | undefined>(() => (
    savedDraft ? {
      filters: savedDraft,
      bandCombo: showBandComboSection ? bandComboController.savedDraft : [],
    } : undefined
  ), [bandComboController.savedDraft, savedDraft, showBandComboSection]);

  const { hasChanges, confirmOpen, setConfirmOpen, handleClose } = useModalDraft(modalDraft, modalSavedDraft, onCancel, areFilterModalDraftsEqual);
  const bandComboBlocksApply = showBandComboSection && bandComboController.hasChanges && bandComboController.applyDisabled;

  const handleReset = useCallback(() => {
    onReset();
    if (showBandComboSection) bandComboController.resetDraft();
  }, [bandComboController, onReset, showBandComboSection]);

  const handleApply = useCallback(() => {
    if (showBandComboSection && bandComboController.hasChanges && !bandComboController.apply()) return;
    onApply();
  }, [bandComboController, onApply, showBandComboSection]);

  return (
    <Modal visible={visible} title={t('common.filterSongs')} onClose={handleClose} onApply={handleApply} onReset={handleReset} resetLabel={t('filter.resetLabel')} resetHint={t('filter.resetHint')} applyLabel={t('filter.applyLabel')} applyDisabled={!hasChanges || bandComboBlocksApply} afterPanel={showBandComboSection && bandComboController.pendingInvalidSelection ? (
        <BandInstrumentFilterInvalidSelectionAlert controller={bandComboController} />
      ) : confirmOpen ? (
        <ConfirmAlert
          title={t('filter.cancelTitle')}
          message={t('filter.cancelMessage')}
          onNo={() => setConfirmOpen(false)}
          onYes={onCancel}
          onExitComplete={() => setConfirmOpen(false)}
        />
      ) : null}>
      {showBandComboSection ? (
        <BandInstrumentFilterPicker controller={bandComboController} compact />
      ) : null}

      {selectedBandMode ? (
        <ModalSection title={t('filter.selectedBandScores')} hint={t('filter.selectedBandScoresHint', { band: selectedBandName ?? t('band.title') })}>
          <ToggleRow
            label={t('filter.selectedBandHasScore')}
            description={t('filter.selectedBandHasScoreDesc')}
            checked={draft.selectedBandHasScore}
            onToggle={toggleSelectedBandHasScore}
          />
          <ToggleRow
            label={t('filter.selectedBandMissingScore')}
            description={t('filter.selectedBandMissingScoreDesc')}
            checked={draft.selectedBandMissingScore}
            onToggle={toggleSelectedBandMissingScore}
          />
        </ModalSection>
      ) : (<>
        {/* Global filters */}
        <ModalSection>
          <Accordion title={t('filter.globalToggles')} hint={t('filter.globalTogglesHint')}>
            <ToggleRow
              label={t('filter.missingScores')}
              description={t('filter.missingScoresDesc')}
              checked={allOn(draft.missingScores)}
              onToggle={() => toggleGlobal('missingScores')}
            />
            <ToggleRow
              label={t('filter.hasScores')}
              description={t('filter.hasScoresDesc')}
              checked={allOn(draft.hasScores)}
              onToggle={() => toggleGlobal('hasScores')}
            />
            <ToggleRow
              label={t('filter.missingFCs')}
              description={t('filter.missingFCsDesc')}
              checked={allOn(draft.missingFCs)}
              onToggle={() => toggleGlobal('missingFCs')}
            />
            <ToggleRow
              label={t('filter.hasFCs')}
              description={t('filter.hasFCsDesc')}
              checked={allOn(draft.hasFCs)}
              onToggle={() => toggleGlobal('hasFCs')}
            />
            {appSettings.filterInvalidScores && (
              <ToggleRow
                label={t('filter.overThreshold')}
                description={t('filter.overThresholdDesc')}
                checked={allOn(draft.overThreshold ?? {})}
                onToggle={() => toggleGlobal('overThreshold')}
              />
            )}
          </Accordion>
        </ModalSection>

        {/* Missing filters nstrument accordions */}
        <ModalSection title={t('filter.individualToggles')} hint={t('filter.individualTogglesHint')}>
          {visibleKeys.map(key => (
            <Accordion key={key} title={INSTRUMENT_LABELS[key]} icon={<InstrumentIcon instrument={key} size={28} />}>
              <ToggleRow
                label={t('filter.instrumentMissingScores', { instrument: INSTRUMENT_LABELS[key] })}
                description={t('filter.instrumentMissingScoresDesc', { instrument: INSTRUMENT_LABELS[key] })}
                checked={draft.missingScores[key] ?? false}
                onToggle={() => toggleMissingScores(key)}
              />
              <ToggleRow
                label={t('filter.instrumentHasScores', { instrument: INSTRUMENT_LABELS[key] })}
                description={t('filter.instrumentHasScoresDesc', { instrument: INSTRUMENT_LABELS[key] })}
                checked={draft.hasScores[key] ?? false}
                onToggle={() => toggleHasScores(key)}
              />
              <ToggleRow
                label={t('filter.instrumentMissingFCs', { instrument: INSTRUMENT_LABELS[key] })}
                description={t('filter.instrumentMissingFCsDesc', { instrument: INSTRUMENT_LABELS[key] })}
                checked={draft.missingFCs[key] ?? false}
                onToggle={() => toggleMissingFCs(key)}
              />
              <ToggleRow
                label={t('filter.instrumentHasFCs', { instrument: INSTRUMENT_LABELS[key] })}
                description={t('filter.instrumentHasFCsDesc', { instrument: INSTRUMENT_LABELS[key] })}
                checked={draft.hasFCs[key] ?? false}
                onToggle={() => toggleHasFCs(key)}
              />
              {appSettings.filterInvalidScores && (
                <ToggleRow
                  label={t('filter.instrumentOverThreshold', { instrument: INSTRUMENT_LABELS[key] })}
                  description={t('filter.instrumentOverThresholdDesc', { instrument: INSTRUMENT_LABELS[key] })}
                  checked={draft.overThreshold?.[key] ?? false}
                  onToggle={() => toggleOverThreshold(key)}
                />
              )}
            </Accordion>
          ))}
        </ModalSection>
      </>)}

      {showIndividualBandMemberFilters ? (
        <ModalSection>
          <Accordion title={t('filter.individualBandMemberFilters')} hint={t('filter.individualBandMemberFiltersHint')}>
            {selectedBandMembers.map(member => {
              const memberFilter = draft.individualBandMemberScoreFilters[member.accountId] ?? {};
              return (
                <div key={member.accountId}>
                  <ToggleRow
                    label={t('filter.individualBandMemberHasScore', { bandmate: member.displayName })}
                    description={t('filter.individualBandMemberHasScoreDesc', { bandmate: member.displayName })}
                    checked={memberFilter.hasScore ?? false}
                    onToggle={() => toggleIndividualBandMemberHasScore(member.accountId)}
                  />
                  <ToggleRow
                    label={t('filter.individualBandMemberMissingScore', { bandmate: member.displayName })}
                    description={t('filter.individualBandMemberMissingScoreDesc', { bandmate: member.displayName })}
                    checked={memberFilter.missingScore ?? false}
                    onToggle={() => toggleIndividualBandMemberMissingScore(member.accountId)}
                  />
                </div>
              );
            })}
          </Accordion>
        </ModalSection>
      ) : null}

      {/* Item Shop filters */}
      {isShopVisible && (
        <ModalSection>
          <Accordion title={t('filter.shopTitle')} hint={t('filter.shopHint')}>
            <ToggleRow
              label={t('filter.shopInShop')}
              description={t('filter.shopInShopDesc')}
              checked={draft.shopInShop}
              onToggle={() => onChange({ ...draft, shopInShop: !draft.shopInShop })}
            />
            <ToggleRow
              label={t('filter.shopLeavingTomorrow')}
              description={t('filter.shopLeavingTomorrowDesc')}
              checked={draft.shopLeavingTomorrow}
              onToggle={() => onChange({ ...draft, shopLeavingTomorrow: !draft.shopLeavingTomorrow })}
            />
          </Accordion>
        </ModalSection>
      )}

      {/* Instrument selector */}
      {!selectedBandMode && (
        <ModalSection title={t('filter.instrumentFilters')} hint={t('filter.instrumentFiltersHint')}>
          <InstrumentSelector
            instruments={selectorItems}
            selected={draft.instrumentFilter}
            onSelect={handleInstrumentSelect}
            deferSelection
          >
            <ModalSection>
              <Accordion title={t('filter.seasonTitle')} hint={t('filter.seasonHint')}>
                <SeasonToggles
                  availableSeasons={availableSeasons}
                  seasonFilter={draft.seasonFilter}
                  onChange={seasonFilter => onChange({ ...draft, seasonFilter })}
                />
              </Accordion>
            </ModalSection>

            <ModalSection>
              <Accordion title={t('filter.percentileTitle')} hint={t('filter.percentileHint')}>
                <PercentileToggles
                  percentileFilter={draft.percentileFilter}
                  onChange={percentileFilter => onChange({ ...draft, percentileFilter })}
                />
              </Accordion>
            </ModalSection>

            <ModalSection>
              <Accordion title={t('filter.starsTitle')} hint={t('filter.starsHint')}>
                <StarsToggles
                  starsFilter={draft.starsFilter}
                  onChange={starsFilter => onChange({ ...draft, starsFilter })}
                />
              </Accordion>
            </ModalSection>

            <ModalSection>
              <Accordion title={t('filter.intensityTitle')} hint={t('filter.intensityHint')}>
                <DifficultyToggles
                  difficultyFilter={draft.difficultyFilter}
                  onChange={difficultyFilter => onChange({ ...draft, difficultyFilter })}
                />
              </Accordion>
            </ModalSection>
          </InstrumentSelector>
        </ModalSection>
      )}
    </Modal>
  );
}

function areFilterModalDraftsEqual(a: FilterModalDraftState, b: FilterModalDraftState) {
  return JSON.stringify(a.filters) === JSON.stringify(b.filters)
    && areBandInstrumentDraftsEqual(a.bandCombo, b.bandCombo);
}

/* -- Toggle components for composite filters -- */

function SeasonToggles({ availableSeasons, seasonFilter, onChange }: { availableSeasons: number[]; seasonFilter: Record<number, boolean>; onChange: (f: Record<number, boolean>) => void }) {
  const { t } = useTranslation();
  const seasons = [...availableSeasons, 0];

  const isOn = (s: number) => seasonFilter[s] !== false;
  const toggleSeason = (s: number) => onChange({ ...seasonFilter, [s]: !isOn(s) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of seasons) next[s] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of seasons) next[s] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {seasons.map(s => (
        <ToggleRow key={s} label={s === 0 ? t('filter.noScore') : t('filter.seasonLabel', { season: s })} checked={isOn(s)} onToggle={() => toggleSeason(s)} />
      ))}
    </>
  );
}

/* v8 ignore start -- V8 misses inline callbacks inside un-exported sub-components; covered via ModalCallbacks tests */
function PercentileToggles({ percentileFilter, onChange }: { percentileFilter: Record<number, boolean>; onChange: (f: Record<number, boolean>) => void }) {
  const { t } = useTranslation();
  const allKeys = [0, ...PERCENTILE_THRESHOLDS];
  const isOn = (p: number) => percentileFilter[p] !== false;
  const toggleP = (p: number) => onChange({ ...percentileFilter, [p]: !isOn(p) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const p of allKeys) next[p] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const p of allKeys) next[p] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {allKeys.map(p => (
        <ToggleRow key={p} label={p === 0 ? t('filter.noScore') : t('filter.topPercent', { percent: p })} checked={isOn(p)} onToggle={() => toggleP(p)} />
      ))}
    </>
  );
}
function StarsToggles({ starsFilter, onChange }: { starsFilter: Record<number, boolean>; onChange: (f: Record<number, boolean>) => void }) {
  const { t } = useTranslation();
  const allKeys = [6, 5, 4, 3, 2, 1, 0];

  const starLabel = (k: number) => {
    if (k === 0) return t('filter.noScore');
    const isGold = k === 6;
    const count = isGold ? 5 : k;
    const src = isGold ? `${import.meta.env.BASE_URL}star_gold.png` : `${import.meta.env.BASE_URL}star_white.png`;
    return (
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
        {Array.from({ length: count }, (_, i) => (
          <img key={i} src={src} alt="" width={14} height={14} />
        ))}
      </span>
    );
  };

  const isOn = (s: number) => starsFilter[s] !== false;
  const toggleS = (s: number) => onChange({ ...starsFilter, [s]: !isOn(s) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of allKeys) next[s] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const s of allKeys) next[s] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {allKeys.map(s => (
        <ToggleRow key={s} label={starLabel(s)} checked={isOn(s)} onToggle={() => toggleS(s)} />
      ))}
    </>
  );
}

/* v8 ignore stop */

function DifficultyToggles({ difficultyFilter, onChange }: { difficultyFilter: Record<number, boolean>; onChange: (f: Record<number, boolean>) => void }) {
  const { t } = useTranslation();
  const allKeys = [1, 2, 3, 4, 5, 6, 7, 0];
  const diffLabel = (k: number): React.ReactNode =>
    k === 0 ? t('filter.noScore') : <DifficultyBars level={k} />;

  const isOn = (d: number) => difficultyFilter[d] !== false;
  const toggleD = (d: number) => onChange({ ...difficultyFilter, [d]: !isOn(d) });
  const selectAll = () => {
    const next: Record<number, boolean> = {};
    for (const d of allKeys) next[d] = true;
    onChange(next);
  };
  const clearAll = () => {
    const next: Record<number, boolean> = {};
    for (const d of allKeys) next[d] = false;
    onChange(next);
  };

  return (
    <>
      <BulkActions onSelectAll={selectAll} onClearAll={clearAll} />
      {allKeys.map(d => (
        <ToggleRow key={d} label={diffLabel(d)} checked={isOn(d)} onToggle={() => toggleD(d)} />
      ))}
    </>
  );
}
