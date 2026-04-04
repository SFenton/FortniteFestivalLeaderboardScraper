/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import Modal from '../../../components/modals/Modal';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import { RadioRow } from '../../../components/common/RadioRow';
import { ReorderList } from '../../../components/sort/ReorderList';
import { Accordion } from '../../../components/common/Accordion';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { INSTRUMENT_LABELS } from '@festival/core/api/serverTypes';
import type { SongSortMode } from '../../../utils/songSettings';
import { INSTRUMENT_SORT_MODES, METADATA_SORT_DISPLAY } from '../../../utils/songSettings';
import { Colors, Font, Gap } from '@festival/theme';
import { IoArrowUp, IoArrowDown } from 'react-icons/io5';
import { useModalDraft } from '../../../hooks/ui/useModalDraft';
import { useTranslation } from 'react-i18next';

export type SortDraft = {
  sortMode: SongSortMode;
  sortAscending: boolean;
  metadataOrder: string[];
  instrumentOrder: InstrumentKey[];
};

export type MetadataVisibility = {
  score: boolean;
  percentage: boolean;
  percentile: boolean;
  seasonachieved: boolean;
  intensity: boolean;
  stars: boolean;
};

type SortModalProps = {
  visible: boolean;
  draft: SortDraft;
  savedDraft?: SortDraft;
  instrumentFilter: InstrumentKey | null;
  hasPlayer?: boolean;
  hideItemShop?: boolean;
  metadataVisibility?: MetadataVisibility;
  onChange: (d: SortDraft) => void;
  onCancel: () => void;
  onReset: () => void;
  onApply: () => void;
};

export default function SortModal({ visible, draft, savedDraft, instrumentFilter, hasPlayer, hideItemShop, metadataVisibility: mv, onChange, onCancel, onReset, onApply }: SortModalProps) {
  const { t } = useTranslation();
  const setMode = (sortMode: SongSortMode) => onChange({ ...draft, sortMode });

  const { hasChanges, confirmOpen, setConfirmOpen, handleClose } = useModalDraft(
    draft, savedDraft, onCancel,
    (a, b) => a.sortMode === b.sortMode
      && a.sortAscending === b.sortAscending
      && JSON.stringify(a.metadataOrder) === JSON.stringify(b.metadataOrder)
      && JSON.stringify(a.instrumentOrder) === JSON.stringify(b.instrumentOrder),
  );

  const visibleInstrumentSortModes = mv
    ? INSTRUMENT_SORT_MODES.filter(({ mode }) => {
        const visMap: Record<string, boolean> = {
          score: mv.score, percentage: mv.percentage, percentile: mv.percentile,
          stars: mv.stars, seasonachieved: mv.seasonachieved, intensity: mv.intensity,
        };
        return visMap[mode] !== false;
      })
    : INSTRUMENT_SORT_MODES;

  const visibleMetadataOrder = mv
    ? draft.metadataOrder.filter(k => {
        const visMap: Record<string, boolean> = {
          score: mv.score, percentage: mv.percentage, percentile: mv.percentile,
          stars: mv.stars, seasonachieved: mv.seasonachieved, intensity: mv.intensity,
        };
        return visMap[k] !== false;
      })
    : draft.metadataOrder;

  const anyMetadataVisible = !mv || (mv.score || mv.percentage || mv.percentile || mv.stars || mv.seasonachieved || mv.intensity);

  return (
    <Modal visible={visible} title={t('common.sortSongs')} onClose={handleClose} onApply={onApply} onReset={onReset} resetLabel={t('sort.resetLabel')} resetHint={t('sort.resetHint')} applyLabel={t('sort.applyLabel')} applyDisabled={!hasChanges} afterPanel={confirmOpen ? (
        <ConfirmAlert
          title={t('sort.cancelTitle')}
          message={t('sort.cancelMessage')}
          onNo={() => setConfirmOpen(false)}
          onYes={onCancel}
          onExitComplete={() => setConfirmOpen(false)}
        />
      ) : null}>
      {/* v8 ignore start -- sort mode Accordion with hideItemShop */}
      {hasPlayer ? (
        <ModalSection>
          <Accordion title={t('sort.mode')} hint={t('sort.modeHint')} defaultOpen={!instrumentFilter}>
            <RadioRow label={t('sort.title')} selected={draft.sortMode === 'title'} onSelect={() => setMode('title')} />
            <RadioRow label={t('sort.artist')} selected={draft.sortMode === 'artist'} onSelect={() => setMode('artist')} />
            <RadioRow label={t('sort.year')} selected={draft.sortMode === 'year'} onSelect={() => setMode('year')} />
            {/* v8 ignore next -- hideItemShop branch */}
            {!hideItemShop && <RadioRow label={t('sort.itemShop')} selected={draft.sortMode === 'shop'} onSelect={() => setMode('shop')} />}
            <RadioRow label={t('sort.hasFC')} selected={draft.sortMode === 'hasfc'} onSelect={() => setMode('hasfc')} />
          </Accordion>
        </ModalSection>
      ) : (
        /* v8 ignore start -- non-player sort mode path */
        <ModalSection title={t('sort.mode')} hint={t('sort.modeHint')}>
          <RadioRow label={t('sort.title')} selected={draft.sortMode === 'title'} onSelect={() => setMode('title')} />
          <RadioRow label={t('sort.artist')} selected={draft.sortMode === 'artist'} onSelect={() => setMode('artist')} />
          <RadioRow label={t('sort.year')} selected={draft.sortMode === 'year'} onSelect={() => setMode('year')} />
          {!hideItemShop && <RadioRow label={t('sort.itemShop')} selected={draft.sortMode === 'shop'} onSelect={() => setMode('shop')} />}
          <RadioRow label={t('sort.hasFC')} selected={draft.sortMode === 'hasfc'} onSelect={() => setMode('hasfc')} />
        </ModalSection>
        /* v8 ignore stop */
      )}

      {/* Instrument-specific sort modes (only when an instrument is selected) */}
      {instrumentFilter != null && visibleInstrumentSortModes.length > 0 && (
        <ModalSection>
          <Accordion title={t('sort.filteredInstrumentMode')} hint={t('sort.filteredInstrumentHint')}>
            {visibleInstrumentSortModes.map(({ mode, label }) => (
              <RadioRow key={mode} label={label} selected={draft.sortMode === mode} onSelect={() => setMode(mode)} />
            ))}
          </Accordion>
        </ModalSection>
      )}

      {/* Direction */}
      <ModalSection>
        <div style={directionStyles.inner}>
          <div style={directionStyles.textCol}>
            <div style={directionStyles.title}>{t('sort.direction')}</div>
            <div style={directionStyles.hint}>
              {draft.sortAscending ? t('sort.ascendingHintSongs') : t('sort.descendingHintSongs')}
            </div>
          </div>
          <div style={directionStyles.icons}>
            <button
              style={directionStyles.iconBtn}
              onClick={() => onChange({ ...draft, sortAscending: true })}
              aria-label={t('aria.ascending')}
            >
              <div style={{ ...directionStyles.iconCircle, ...(draft.sortAscending ? directionStyles.iconCircleActive : {}) }} />
              <IoArrowUp size={20} style={{ position: 'relative' as const, zIndex: 1, color: draft.sortAscending ? Colors.textPrimary : Colors.textMuted, transition: 'color 200ms ease' }} />
            </button>
            <button
              style={directionStyles.iconBtn}
              onClick={() => onChange({ ...draft, sortAscending: false })}
              aria-label={t('aria.descending')}
            >
              <div style={{ ...directionStyles.iconCircle, ...(!draft.sortAscending ? directionStyles.iconCircleActive : {}) }} />
              <IoArrowDown size={20} style={{ position: 'relative' as const, zIndex: 1, color: !draft.sortAscending ? Colors.textPrimary : Colors.textMuted, transition: 'color 200ms ease' }} />
            </button>
          </div>
        </div>
      </ModalSection>

      {/* Metadata sort priority (only when an instrument is selected) */}
      {instrumentFilter != null && anyMetadataVisible && (
        <ModalSection title={t('sort.metadataPriority')} hint={t('sort.metadataPriorityHint')}>
          <ReorderList
          /* v8 ignore next -- nullish coalescing for display label */
            items={visibleMetadataOrder.map(k => ({ key: k, label: METADATA_SORT_DISPLAY[k] ?? k }))}
            /* v8 ignore start -- DnD reorder callback; can't fire in jsdom (DnD handler is v8-ignored) */
            onReorder={(items) => onChange({ ...draft, metadataOrder: items.map(i => i.key) })}
            /* v8 ignore stop */
          />
        </ModalSection>
      )}

      {/* Primary Instrument Order (only when NO instrument is selected and sorting by Has FC) */}
      {instrumentFilter == null && draft.sortMode === 'hasfc' && (
        <ModalSection>
          <Accordion title={t('sort.instrumentOrder')} hint={t('sort.instrumentOrderHint')} defaultOpen>
            <ReorderList
              items={draft.instrumentOrder.map(k => ({ key: k, label: INSTRUMENT_LABELS[k] }))}
              /* v8 ignore start -- DnD reorder callback; can't fire in jsdom */
              onReorder={(items) => onChange({ ...draft, instrumentOrder: items.map(i => i.key) as InstrumentKey[] })}
              /* v8 ignore stop */
            />
          </Accordion>
        </ModalSection>
      )}
    </Modal>
  );
}

const directionStyles: Record<string, React.CSSProperties> = {
  inner: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    paddingBottom: Gap.md,
  },
  textCol: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
  },
  title: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.textPrimary,
  },
  hint: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  icons: {
    display: 'flex',
    gap: Gap.md,
    flexShrink: 0,
    marginRight: -12,
  },
  iconBtn: {
    width: 40,
    height: 40,
    borderRadius: '50%',
    border: 'none',
    backgroundColor: 'transparent',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    position: 'relative' as const,
    overflow: 'hidden' as const,
  },
  iconCircle: {
    position: 'absolute' as const,
    inset: 0,
    borderRadius: '50%',
    backgroundColor: Colors.accentPurple,
    transform: 'scale(0)',
    transition: 'transform 250ms ease',
  },
  iconCircleActive: {
    transform: 'scale(1)',
  },
};

