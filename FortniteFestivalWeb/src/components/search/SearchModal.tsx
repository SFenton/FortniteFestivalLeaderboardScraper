import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { LoadPhase } from '@festival/core/runtime';
import { DEFAULT_INSTRUMENT, type AccountSearchResult, type BandSearchResult, type PlayerBandEntry, type ServerInstrumentKey } from '@festival/core/api';
import { staggerDelay } from '@festival/ui-utils';
import SearchBar, { type SearchBarRef } from '../common/SearchBar';
import ArcSpinner, { SpinnerSize } from '../common/ArcSpinner';
import ModalShell from '../modals/components/ModalShell';
import PlayerBandCard from '../../pages/player/components/PlayerBandCard';
import { SongRow } from '../../pages/songs/components/SongRow';
import { useUnifiedSearch } from '../../hooks/data/useUnifiedSearch';
import { useSelectedProfile } from '../../hooks/data/useSelectedProfile';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useScrollFade } from '../../hooks/ui/useScrollFade';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { usePressAction } from '../../hooks/ui/usePressAction';
import { markTapDiagnosticsAction, type TapDiagnosticsActionRecord } from '../../diagnostics/tapDiagnostics';
import { Routes } from '../../routes';
import { SEARCH_TARGETS, type SearchTarget } from '../../types/search';
import { paddingWithSafeAreaBottom } from '../../utils/safeAreaStyles';
import {
  Align, Border, BoxSizing, Colors, Cursor, CssValue, Display, Font, Gap, Justify,
  LineHeight, Overflow, Radius, TextAlign, TextTransform, Weight, flexCenter, flexColumn,
  border, frostedCard, padding, Shadow, FADE_DURATION, QUICK_FADE_MS, SPINNER_FADE_MS, STAGGER_INTERVAL,
} from '@festival/theme';

const SEARCH_MODAL_DESKTOP: CSSProperties = { width: 520, height: 640, maxHeight: '90vh' };
const MODAL_TRANSITION_MS = 250;
const SEARCH_STAGGER_VISIBLE_ITEMS = 8;
const SEARCH_SCROLL_FADE_SIZE = 40;
const SEARCH_KEYBOARD_CLEARANCE = 12;
const SEARCH_KEYBOARD_OPEN_THRESHOLD = 120;
const SEARCH_KEYBOARD_CLOSED_THRESHOLD = 80;
const SEARCH_KEYBOARD_DIRECTION_DEADBAND = 8;
const SEARCH_KEYBOARD_LARGE_CLOSING_DELTA = 64;
const SEARCH_KEYBOARD_INSET_DEADBAND = 8;
const SEARCH_KEYBOARD_OPEN_SAMPLE_COUNT = 2;
const SEARCH_VIEWPORT_SCALE_TOLERANCE = 0.01;
const SEARCH_VIEWPORT_WIDTH_TOLERANCE = 8;
const SEARCH_MODAL_MOBILE_ENTER_OFFSET = 0;

const SEARCH_TARGET_LABEL_KEYS: Record<SearchTarget, string> = {
  songs: 'search.tabs.songs',
  players: 'search.tabs.players',
  bands: 'search.tabs.bands',
};

const EMPTY_INSTRUMENTS: ServerInstrumentKey[] = [];
const EMPTY_METADATA_ORDER: string[] = [];

interface SearchModalProps {
  visible: boolean;
  onClose: () => void;
  availableTargets?: readonly SearchTarget[];
  placeholderKey?: string;
  onPlayerSelect?: (player: AccountSearchResult) => void;
}

type SearchViewKey = SearchTarget | 'global';
type KeyboardPhase = 'unknown' | 'opening' | 'open' | 'closing' | 'closed';
type SearchModalTransition = 'closed' | 'opening' | 'open' | 'closing';

const SEARCH_PLACEHOLDER_KEYS: Record<string, string> = {
  songs: 'search.placeholders.songs',
  players: 'search.placeholders.players',
  bands: 'search.placeholders.bands',
  'songs|players': 'search.placeholders.songsPlayers',
  'songs|bands': 'search.placeholders.songsBands',
  'players|bands': 'search.placeholders.playersBands',
  'songs|players|bands': 'search.placeholders.songsPlayersBands',
};

function resolveSearchTargets(availableTargets?: readonly SearchTarget[]): readonly SearchTarget[] {
  if (!availableTargets || availableTargets.length === 0) return SEARCH_TARGETS;
  const allowed = new Set<SearchTarget>(availableTargets);
  const resolved = SEARCH_TARGETS.filter(target => allowed.has(target));
  return resolved.length > 0 ? resolved : SEARCH_TARGETS;
}

function getSearchPlaceholderKey(targets: readonly SearchTarget[]): string {
  return SEARCH_PLACEHOLDER_KEYS[targets.join('|')] ?? 'search.placeholder';
}

export default function SearchModal({ visible, onClose, availableTargets, placeholderKey, onPlayerSelect }: SearchModalProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { profile: selectedProfile } = useSelectedProfile();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const inputRef = useRef<SearchBarRef>(null);
  const resultsRef = useRef<HTMLDivElement>(null);
  const mobileFiltersRef = useRef<HTMLDivElement>(null);
  const wasVisibleRef = useRef(false);
  const keyboardBaselineRef = useRef<number | null>(null);
  const filterBottomBaselineRef = useRef<number | null>(null);
  const keyboardInsetRef = useRef(0);
  const keyboardInsetFrameRef = useRef<number | null>(null);
  const lastSearchPointerTypeRef = useRef('');
  const searchInputWasFocusedOnPointerDownRef = useRef(false);
  const keyboardPhaseRef = useRef<KeyboardPhase>('unknown');
  const lastKeyboardLossRef = useRef<number | null>(null);
  const keyboardOpenSampleCountRef = useRef(0);
  const keyboardClosingSampleCountRef = useRef(0);
  const keyboardWasEverOpenRef = useRef(false);
  const keyboardBaselineWidthRef = useRef<number | null>(null);
  const pointerDownKeyboardLossRef = useRef<number | null>(null);
  const intentionalRefocusRef = useRef(false);
  const reactivationArmedRef = useRef(false);
  const isComposingRef = useRef(false);
  const pendingClickPointerTypeRef = useRef('');
  const pendingClickInputWasFocusedRef = useRef(false);
  const suppressCompatibilityBlurRef = useRef(false);
  const modalTransitionRef = useRef<SearchModalTransition>(visible ? 'opening' : 'closed');
  const viewportEventSequenceRef = useRef(0);
  const visibleTargets = useMemo(() => resolveSearchTargets(availableTargets), [availableTargets]);
  const resolvedPlaceholderKey = placeholderKey ?? getSearchPlaceholderKey(visibleTargets);
  const showTargetTabs = visibleTargets.length > 1;
  const [query, setQuery] = useState('');
  const [activeTarget, setActiveTarget] = useState<SearchTarget | null>(null);
  const [searchFocused, setSearchFocused] = useState(false);
  const [keyboardInset, setKeyboardInset] = useState(0);
  const diagnosticVisibleRef = useRef(visible);
  const diagnosticSearchFocusedRef = useRef(searchFocused);
  const diagnosticScopeRef = useRef(visibleTargets.join('|'));
  diagnosticVisibleRef.current = visible;
  diagnosticSearchFocusedRef.current = searchFocused;
  diagnosticScopeRef.current = visibleTargets.join('|');
  const st = useStyles(isMobile, keyboardInset);
  const effectiveActiveTarget = showTargetTabs ? activeTarget : visibleTargets[0] ?? null;
  const search = useUnifiedSearch(query, { enabledTargets: visibleTargets });

  const recordSearchDiagnostic = useCallback((
    label: string,
    phase: TapDiagnosticsActionRecord['phase'] = 'note',
    details: Record<string, unknown> = {},
  ) => {
    if (typeof window === 'undefined' || !window.__fstTapDiagnostics) return;
    const visualViewport = window.visualViewport;
    const baseline = keyboardBaselineRef.current;
    const visualViewportHeight = visualViewport?.height ?? window.innerHeight;
    const keyboardLoss = baseline == null
      ? null
      : Math.max(
        0,
        Math.round(baseline - visualViewportHeight),
        Math.round(baseline - window.innerHeight),
      );
    const activeElement = document.activeElement;
    markTapDiagnosticsAction(`search:${label}`, phase, {
      scope: diagnosticScopeRef.current,
      modalVisible: diagnosticVisibleRef.current,
      modalTransition: modalTransitionRef.current,
      searchFocused: diagnosticSearchFocusedRef.current,
      keyboardPhase: keyboardPhaseRef.current,
      keyboardLoss,
      keyboardBaseline: baseline,
      lastKeyboardLoss: lastKeyboardLossRef.current,
      keyboardOpenSamples: keyboardOpenSampleCountRef.current,
      keyboardClosingSamples: keyboardClosingSampleCountRef.current,
      keyboardWasEverOpen: keyboardWasEverOpenRef.current,
      keyboardInset: keyboardInsetRef.current,
      viewportEventSequence: viewportEventSequenceRef.current,
      visualViewportHeight,
      visualViewportWidth: visualViewport?.width ?? window.innerWidth,
      visualViewportOffsetTop: visualViewport?.offsetTop ?? 0,
      visualViewportScale: visualViewport?.scale ?? 1,
      innerHeight: window.innerHeight,
      clientHeight: document.documentElement.clientHeight,
      pointerType: pendingClickPointerTypeRef.current || lastSearchPointerTypeRef.current,
      inputFocusedAtPointerDown: pendingClickPointerTypeRef.current
        ? pendingClickInputWasFocusedRef.current
        : searchInputWasFocusedOnPointerDownRef.current,
      pointerDownKeyboardLoss: pointerDownKeyboardLossRef.current,
      reactivationArmed: reactivationArmedRef.current,
      intentionalRefocus: intentionalRefocusRef.current,
      suppressCompatibilityBlur: suppressCompatibilityBlurRef.current,
      composing: isComposingRef.current,
      activeElementTag: activeElement?.tagName ?? null,
      activeElementRole: activeElement instanceof HTMLElement ? activeElement.getAttribute('role') : null,
      ...details,
    });
  }, []);

  const captureKeyboardBaseline = useCallback(() => {
    if (keyboardBaselineRef.current != null) return;
    const visualViewport = window.visualViewport;
    const visualViewportBottom = visualViewport ? visualViewport.height + visualViewport.offsetTop : 0;
    keyboardBaselineRef.current = Math.max(
      window.innerHeight || 0,
      document.documentElement.clientHeight || 0,
      visualViewportBottom,
    );
    keyboardBaselineWidthRef.current = visualViewport?.width ?? window.innerWidth;
    lastKeyboardLossRef.current = 0;
    recordSearchDiagnostic('baseline-captured', 'note');
  }, [recordSearchDiagnostic]);

  const resetKeyboardState = useCallback(() => {
    keyboardBaselineRef.current = null;
    keyboardBaselineWidthRef.current = null;
    filterBottomBaselineRef.current = null;
    keyboardInsetRef.current = 0;
    keyboardPhaseRef.current = 'unknown';
    lastKeyboardLossRef.current = null;
    keyboardOpenSampleCountRef.current = 0;
    keyboardClosingSampleCountRef.current = 0;
    keyboardWasEverOpenRef.current = false;
    pointerDownKeyboardLossRef.current = null;
    intentionalRefocusRef.current = false;
    reactivationArmedRef.current = false;
    isComposingRef.current = false;
    pendingClickPointerTypeRef.current = '';
    pendingClickInputWasFocusedRef.current = false;
    suppressCompatibilityBlurRef.current = false;
    if (keyboardInsetFrameRef.current != null) {
      window.cancelAnimationFrame(keyboardInsetFrameRef.current);
      keyboardInsetFrameRef.current = null;
    }
    setKeyboardInset(0);
  }, []);

  useEffect(() => {
    if (visible && !wasVisibleRef.current) {
      modalTransitionRef.current = 'opening';
      recordSearchDiagnostic('modal-transition', 'start', { transitionTo: 'opening' });
      setActiveTarget(null);
    } else if (visible && activeTarget && !visibleTargets.includes(activeTarget)) {
      setActiveTarget(null);
    }
    if (!visible) {
      modalTransitionRef.current = wasVisibleRef.current ? 'closing' : 'closed';
      recordSearchDiagnostic('modal-transition', 'start', {
        transitionTo: modalTransitionRef.current,
      });
      lastSearchPointerTypeRef.current = '';
      searchInputWasFocusedOnPointerDownRef.current = false;
      setQuery('');
      setSearchFocused(false);
      resetKeyboardState();
    }
    wasVisibleRef.current = visible;
  }, [activeTarget, recordSearchDiagnostic, resetKeyboardState, visible, visibleTargets]);

  const focusSearchWithoutScroll = useCallback(() => {
    captureKeyboardBaseline();
    setSearchFocused(true);
    inputRef.current?.focus({ preventScroll: true });
  }, [captureKeyboardBaseline]);

  const readKeyboardLoss = useCallback((): number | null => {
    const baseline = keyboardBaselineRef.current;
    const baselineWidth = keyboardBaselineWidthRef.current;
    if (baseline == null || baselineWidth == null) return null;

    const visualViewport = window.visualViewport;
    const scale = visualViewport?.scale ?? 1;
    const width = visualViewport?.width ?? window.innerWidth;
    if (Math.abs(scale - 1) >= SEARCH_VIEWPORT_SCALE_TOLERANCE) {
      keyboardPhaseRef.current = 'unknown';
      lastKeyboardLossRef.current = null;
      keyboardOpenSampleCountRef.current = 0;
      keyboardClosingSampleCountRef.current = 0;
      recordSearchDiagnostic('phase-guard', 'note', {
        guard: 'scale',
        scale,
      });
      return null;
    }
    if (Math.abs(width - baselineWidth) >= SEARCH_VIEWPORT_WIDTH_TOLERANCE) {
      const visualViewportBottom = visualViewport
        ? visualViewport.height + visualViewport.offsetTop
        : 0;
      keyboardBaselineRef.current = Math.max(
        window.innerHeight || 0,
        document.documentElement.clientHeight || 0,
        visualViewportBottom,
      );
      keyboardBaselineWidthRef.current = width;
      keyboardPhaseRef.current = 'unknown';
      lastKeyboardLossRef.current = 0;
      keyboardOpenSampleCountRef.current = 0;
      keyboardClosingSampleCountRef.current = 0;
      keyboardWasEverOpenRef.current = false;
      recordSearchDiagnostic('phase-guard', 'note', {
        guard: 'viewport-width',
        previousWidth: baselineWidth,
        width,
      });
      return null;
    }

    return Math.max(
      0,
      Math.round(baseline - (visualViewport?.height ?? window.innerHeight)),
      Math.round(baseline - window.innerHeight),
    );
  }, [recordSearchDiagnostic]);

  const sampleKeyboardPhase = useCallback((): number | null => {
    const previousPhase = keyboardPhaseRef.current;
    const reportPhaseChange = (reason: string, loss: number | null) => {
      if (keyboardPhaseRef.current === previousPhase) return;
      recordSearchDiagnostic('keyboard-phase', 'note', {
        phaseFrom: previousPhase,
        phaseTo: keyboardPhaseRef.current,
        phaseReason: reason,
        sampledLoss: loss,
      });
    };
    const loss = readKeyboardLoss();
    if (loss == null) {
      reportPhaseChange('invalid-sample', null);
      return null;
    }

    const previousLoss = lastKeyboardLossRef.current;
    lastKeyboardLossRef.current = loss;

    if (loss <= SEARCH_KEYBOARD_CLOSED_THRESHOLD) {
      keyboardPhaseRef.current = keyboardWasEverOpenRef.current ? 'closed' : 'unknown';
      keyboardOpenSampleCountRef.current = 0;
      keyboardClosingSampleCountRef.current = 0;
      reportPhaseChange('closed-threshold', loss);
      return loss;
    }

    if (
      previousLoss != null
      && loss < previousLoss - SEARCH_KEYBOARD_DIRECTION_DEADBAND
      && keyboardWasEverOpenRef.current
    ) {
      keyboardOpenSampleCountRef.current = 0;
      keyboardClosingSampleCountRef.current += 1;
      if (
        previousLoss - loss >= SEARCH_KEYBOARD_LARGE_CLOSING_DELTA
        || keyboardClosingSampleCountRef.current >= SEARCH_KEYBOARD_OPEN_SAMPLE_COUNT
      ) {
        keyboardPhaseRef.current = 'closing';
      }
      reportPhaseChange('loss-decreasing', loss);
      return loss;
    }

    if (previousLoss != null && loss > previousLoss + SEARCH_KEYBOARD_DIRECTION_DEADBAND) {
      keyboardPhaseRef.current = 'opening';
      keyboardClosingSampleCountRef.current = 0;
      if (loss >= SEARCH_KEYBOARD_OPEN_THRESHOLD) {
        keyboardWasEverOpenRef.current = true;
        keyboardOpenSampleCountRef.current = 1;
      }
      reportPhaseChange('loss-increasing', loss);
      return loss;
    }

    if (keyboardPhaseRef.current === 'closing') {
      reportPhaseChange('closing-sticky', loss);
      return loss;
    }

    if (loss >= SEARCH_KEYBOARD_OPEN_THRESHOLD) {
      keyboardWasEverOpenRef.current = true;
      keyboardOpenSampleCountRef.current += 1;
      keyboardClosingSampleCountRef.current = 0;
      if (keyboardOpenSampleCountRef.current >= SEARCH_KEYBOARD_OPEN_SAMPLE_COUNT) {
        keyboardPhaseRef.current = 'open';
      } else {
        keyboardPhaseRef.current = 'opening';
      }
    }

    reportPhaseChange('stable-sample', loss);
    return loss;
  }, [readKeyboardLoss, recordSearchDiagnostic]);

  const handleSearchPointerDown = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    if (intentionalRefocusRef.current || reactivationArmedRef.current) {
      intentionalRefocusRef.current = false;
      reactivationArmedRef.current = false;
      setSearchFocused(false);
      resetKeyboardState();
    }
    lastSearchPointerTypeRef.current = event.pointerType;
    pendingClickPointerTypeRef.current = '';
    pendingClickInputWasFocusedRef.current = false;
    suppressCompatibilityBlurRef.current = false;
    const activeElement = document.activeElement;
    searchInputWasFocusedOnPointerDownRef.current = activeElement instanceof HTMLInputElement
      && event.currentTarget.contains(activeElement);
    suppressCompatibilityBlurRef.current = searchInputWasFocusedOnPointerDownRef.current
      && event.target !== activeElement;
    pointerDownKeyboardLossRef.current = sampleKeyboardPhase();
    reactivationArmedRef.current = false;

    const isTouchPointer = event.pointerType === 'touch' || event.pointerType === 'pen';
    const phase = keyboardPhaseRef.current;
    const shouldArm = (
      isTouchPointer
      && event.isPrimary !== false
      && event.button === 0
      && searchInputWasFocusedOnPointerDownRef.current
      && keyboardWasEverOpenRef.current
      && !isComposingRef.current
      && (phase === 'closing' || phase === 'closed')
    );
    recordSearchDiagnostic('pointerdown', 'note', {
      eventPointerType: event.pointerType,
      eventTargetTag: event.target instanceof Element ? event.target.tagName : null,
      decision: shouldArm ? 'blur-and-arm' : 'observe',
      decisionReason: !isTouchPointer
        ? 'non-touch'
        : !searchInputWasFocusedOnPointerDownRef.current
          ? 'input-not-focused'
          : !keyboardWasEverOpenRef.current
            ? 'keyboard-never-observed'
            : isComposingRef.current
              ? 'composition-active'
              : phase === 'opening' || phase === 'open'
                ? 'keyboard-still-open'
                : 'phase-eligible',
    });
    if (shouldArm) {
      intentionalRefocusRef.current = true;
      reactivationArmedRef.current = true;
      recordSearchDiagnostic('reactivation', 'start', {
        reactivationStage: 'pointerdown-blur',
      });
      inputRef.current?.blur();
    }
  }, [recordSearchDiagnostic, resetKeyboardState, sampleKeyboardPhase]);

  const handleSearchPointerUp = useCallback(() => {
    pendingClickPointerTypeRef.current = lastSearchPointerTypeRef.current;
    pendingClickInputWasFocusedRef.current = searchInputWasFocusedOnPointerDownRef.current;
    lastSearchPointerTypeRef.current = '';
    searchInputWasFocusedOnPointerDownRef.current = false;
    recordSearchDiagnostic('pointerup', 'note', {
      pendingPointerType: pendingClickPointerTypeRef.current,
      pendingInputWasFocused: pendingClickInputWasFocusedRef.current,
    });
  }, [recordSearchDiagnostic]);

  const handleSearchPointerCancel = useCallback(() => {
    const wasArmed = reactivationArmedRef.current;
    lastSearchPointerTypeRef.current = '';
    searchInputWasFocusedOnPointerDownRef.current = false;
    pointerDownKeyboardLossRef.current = null;
    pendingClickPointerTypeRef.current = '';
    pendingClickInputWasFocusedRef.current = false;
    suppressCompatibilityBlurRef.current = false;
    reactivationArmedRef.current = false;
    intentionalRefocusRef.current = false;
    recordSearchDiagnostic('pointercancel', wasArmed ? 'failure' : 'note', {
      cancelledArmedReactivation: wasArmed,
    });
    if (wasArmed) {
      setSearchFocused(false);
      resetKeyboardState();
    }
  }, [recordSearchDiagnostic, resetKeyboardState]);

  const handleSearchClickCapture = useCallback((event: ReactMouseEvent<HTMLDivElement>) => {
    const pointerType = pendingClickPointerTypeRef.current || lastSearchPointerTypeRef.current;
    const inputWasFocused = pendingClickPointerTypeRef.current
      ? pendingClickInputWasFocusedRef.current
      : searchInputWasFocusedOnPointerDownRef.current;
    const pointerDownLoss = pointerDownKeyboardLossRef.current;
    const clickLoss = sampleKeyboardPhase();
    const phase = keyboardPhaseRef.current;
    const closingWithinGesture = pointerDownLoss != null
      && clickLoss != null
      && clickLoss < pointerDownLoss - SEARCH_KEYBOARD_DIRECTION_DEADBAND;
    const isTouchPointer = pointerType === 'touch' || pointerType === 'pen';
    const shouldReactivate = isTouchPointer
      && inputWasFocused
      && keyboardWasEverOpenRef.current
      && !isComposingRef.current
      && (phase === 'closing' || phase === 'closed' || closingWithinGesture);

    recordSearchDiagnostic('click-decision', 'note', {
      clickPointerType: pointerType,
      inputWasFocused,
      clickLoss,
      closingWithinGesture,
      shouldReactivate,
      decisionReason: !isTouchPointer
        ? 'non-touch'
        : !inputWasFocused
          ? 'input-not-focused'
          : !keyboardWasEverOpenRef.current
            ? 'keyboard-never-observed'
            : isComposingRef.current
              ? 'composition-active'
              : phase === 'open' || phase === 'opening'
                ? 'keyboard-still-open'
                : 'phase-eligible',
    });

    if (!reactivationArmedRef.current && shouldReactivate) {
      intentionalRefocusRef.current = true;
      reactivationArmedRef.current = true;
      recordSearchDiagnostic('reactivation', 'start', {
        reactivationStage: 'click-blur',
      });
      inputRef.current?.blur();
    }

    lastSearchPointerTypeRef.current = '';
    searchInputWasFocusedOnPointerDownRef.current = false;
    pointerDownKeyboardLossRef.current = null;
    pendingClickPointerTypeRef.current = '';
    pendingClickInputWasFocusedRef.current = false;
    suppressCompatibilityBlurRef.current = false;
    if (!reactivationArmedRef.current) {
      intentionalRefocusRef.current = false;
      reactivationArmedRef.current = false;
      recordSearchDiagnostic('reactivation', 'note', {
        reactivationStage: 'click-skip',
      });
      return;
    }

    event.preventDefault();
    recordSearchDiagnostic('reactivation', 'note', {
      reactivationStage: 'click-focus',
    });
    focusSearchWithoutScroll();
    keyboardPhaseRef.current = 'opening';
    keyboardOpenSampleCountRef.current = 0;
    reactivationArmedRef.current = false;
    intentionalRefocusRef.current = false;
    recordSearchDiagnostic('reactivation', 'success', {
      reactivationStage: 'complete',
    });
  }, [focusSearchWithoutScroll, recordSearchDiagnostic, sampleKeyboardPhase]);

  const handleSearchFocus = useCallback(() => {
    captureKeyboardBaseline();
    setSearchFocused(true);
    sampleKeyboardPhase();
    recordSearchDiagnostic('focus', 'success');
  }, [captureKeyboardBaseline, recordSearchDiagnostic, sampleKeyboardPhase]);

  const handleSearchBlur = useCallback(() => {
    const suppressed = intentionalRefocusRef.current || suppressCompatibilityBlurRef.current;
    recordSearchDiagnostic('blur', suppressed ? 'note' : 'success', {
      blurSuppressed: suppressed,
      blurReason: intentionalRefocusRef.current
        ? 'intentional-refocus'
        : suppressCompatibilityBlurRef.current
          ? 'compatibility-mousedown'
          : 'genuine-blur',
    });
    if (suppressed) return;
    setSearchFocused(false);
    resetKeyboardState();
  }, [recordSearchDiagnostic, resetKeyboardState]);

  const handleCompositionStart = useCallback(() => {
    isComposingRef.current = true;
    recordSearchDiagnostic('composition', 'start');
  }, [recordSearchDiagnostic]);

  const handleCompositionEnd = useCallback(() => {
    isComposingRef.current = false;
    recordSearchDiagnostic('composition', 'success');
  }, [recordSearchDiagnostic]);

  const handleSearchKeyDown = useCallback((e: React.KeyboardEvent<HTMLInputElement>) => {
    if (isMobileChrome && e.key === 'Enter') {
      inputRef.current?.blur();
    }
  }, [isMobileChrome]);

  const handleCloseComplete = useCallback(() => {
    modalTransitionRef.current = 'closed';
    recordSearchDiagnostic('modal-transition', 'success', { transitionTo: 'closed' });
    setQuery('');
    setActiveTarget(null);
    setSearchFocused(false);
    lastSearchPointerTypeRef.current = '';
    searchInputWasFocusedOnPointerDownRef.current = false;
    resetKeyboardState();
  }, [recordSearchDiagnostic, resetKeyboardState]);

  const updateKeyboardInset = useCallback(() => {
    if (!visible || !isMobileChrome || !searchFocused) {
      keyboardInsetRef.current = 0;
      setKeyboardInset(0);
      return;
    }

    const visualViewport = window.visualViewport;
    const visibleBottom = visualViewport ? visualViewport.height + visualViewport.offsetTop : window.innerHeight;
    const layoutBottom = Math.max(window.innerHeight, document.documentElement.clientHeight);
    const maxInset = Math.max(0, Math.round(layoutBottom - visibleBottom));
    const desiredBottom = visibleBottom - SEARCH_KEYBOARD_CLEARANCE;
    const filtersElement = mobileFiltersRef.current;
    const filterRect = filtersElement?.getBoundingClientRect();
    if (filterBottomBaselineRef.current == null && filterRect && filterRect.bottom > 0) {
      const dialog = filtersElement?.closest('[role="dialog"]') as HTMLElement | null;
      const dialogTransform = dialog ? getComputedStyle(dialog).transform : CssValue.none;
      if (dialogTransform && dialogTransform !== CssValue.none && dialogTransform !== 'matrix(1, 0, 0, 1, 0, 0)') return;
      filterBottomBaselineRef.current = Math.ceil(filterRect.bottom + keyboardInsetRef.current);
    }
    const unshiftedFilterBottom = filterBottomBaselineRef.current;
    const measuredInset = unshiftedFilterBottom == null
      ? maxInset
      : Math.max(0, Math.ceil(unshiftedFilterBottom - desiredBottom));
    const measuredNextInset = Math.min(maxInset, measuredInset);
    const nextInset = measuredNextInset === 0
      || Math.abs(keyboardInsetRef.current - measuredNextInset) >= SEARCH_KEYBOARD_INSET_DEADBAND
      ? measuredNextInset
      : keyboardInsetRef.current;

    if (keyboardInsetRef.current === nextInset) return;
    keyboardInsetRef.current = nextInset;
    setKeyboardInset(nextInset);
  }, [isMobileChrome, searchFocused, visible]);

  const handleOpenComplete = useCallback(() => {
    modalTransitionRef.current = 'open';
    recordSearchDiagnostic('modal-transition', 'success', { transitionTo: 'open' });
    if (!isMobileChrome) focusSearchWithoutScroll();
    updateKeyboardInset();
  }, [focusSearchWithoutScroll, isMobileChrome, recordSearchDiagnostic, updateKeyboardInset]);

  useEffect(() => {
    updateKeyboardInset();

    if (!visible || !isMobileChrome) return undefined;

    const scheduleKeyboardInsetUpdate = () => {
      if (keyboardInsetFrameRef.current != null) return;
      keyboardInsetFrameRef.current = window.requestAnimationFrame(() => {
        keyboardInsetFrameRef.current = null;
        const phaseBefore = keyboardPhaseRef.current;
        sampleKeyboardPhase();
        updateKeyboardInset();
        recordSearchDiagnostic('viewport-rAF', 'note', {
          phaseBefore,
          phaseAfter: keyboardPhaseRef.current,
        });
      });
    };
    const handleViewportChange = (event: Event) => {
      viewportEventSequenceRef.current += 1;
      const phaseBefore = keyboardPhaseRef.current;
      const lossBefore = lastKeyboardLossRef.current;
      sampleKeyboardPhase();
      recordSearchDiagnostic('viewport-event', 'note', {
        viewportEventType: event.type,
        phaseBefore,
        phaseAfter: keyboardPhaseRef.current,
        lossBefore,
        lossAfter: lastKeyboardLossRef.current,
      });
      scheduleKeyboardInsetUpdate();
    };
    const visualViewport = window.visualViewport;
    visualViewport?.addEventListener('resize', handleViewportChange);
    visualViewport?.addEventListener('scroll', handleViewportChange);
    window.addEventListener('resize', handleViewportChange);

    return () => {
      visualViewport?.removeEventListener('resize', handleViewportChange);
      visualViewport?.removeEventListener('scroll', handleViewportChange);
      window.removeEventListener('resize', handleViewportChange);
      if (keyboardInsetFrameRef.current != null) {
        window.cancelAnimationFrame(keyboardInsetFrameRef.current);
        keyboardInsetFrameRef.current = null;
      }
    };
  }, [isMobileChrome, recordSearchDiagnostic, sampleKeyboardPhase, updateKeyboardInset, visible]);

  const closeAndNavigate = useCallback((path: string) => {
    onClose();
    navigate(path);
  }, [navigate, onClose]);

  const handlePlayerSelect = useCallback((player: AccountSearchResult) => {
    if (onPlayerSelect) {
      onClose();
      onPlayerSelect(player);
      return;
    }

    closeAndNavigate(selectedProfile?.type === 'player' && selectedProfile.accountId === player.accountId
      ? Routes.statistics
      : Routes.player(player.accountId));
  }, [closeAndNavigate, onClose, onPlayerSelect, selectedProfile]);

  const handleBandSelect = useCallback((band: BandSearchResult) => {
    if (selectedProfile?.type === 'band'
      && selectedProfile.bandId === band.bandId
      && selectedProfile.bandType === band.bandType
      && selectedProfile.teamKey === band.teamKey) {
      closeAndNavigate(Routes.statistics);
      return;
    }

    const names = band.members.map(member => member.displayName ?? member.accountId).filter(Boolean).join(', ');
    closeAndNavigate(Routes.band(band.bandId, {
      bandType: band.bandType,
      teamKey: band.teamKey,
      names,
    }));
  }, [closeAndNavigate, selectedProfile]);

  const toggleTargetFilter = useCallback((target: SearchTarget) => {
    setActiveTarget(current => current === target ? null : target);
  }, []);

  const tabs = (
    <div ref={mobileFiltersRef} style={st.tabs} role="group" aria-label={t('search.targetTabs')}>
      {visibleTargets.map(target => {
        const selected = activeTarget === target;
        return (
          <SearchTargetTabButton
            key={target}
            target={target}
            label={t(SEARCH_TARGET_LABEL_KEYS[target])}
            selected={selected}
            style={selected ? st.tabSelected : st.tab}
            onToggle={toggleTargetFilter}
          />
        );
      })}
    </div>
  );

  return (
    <ModalShell
      visible={visible}
      title={t('search.title')}
      onClose={onClose}
      desktopStyle={SEARCH_MODAL_DESKTOP}
      mobileEnterOffset={SEARCH_MODAL_MOBILE_ENTER_OFFSET}
      initialFocus={isMobileChrome ? 'panel' : 'first'}
      transitionMs={MODAL_TRANSITION_MS}
      onOpenComplete={handleOpenComplete}
      onCloseComplete={handleCloseComplete}
    >
      <div style={st.body}>
        <SearchBar
          ref={inputRef}
          value={query}
          onChange={setQuery}
          placeholder={t(resolvedPlaceholderKey)}
          onKeyDown={handleSearchKeyDown}
          onFocus={handleSearchFocus}
          onBlur={handleSearchBlur}
          onCompositionStart={handleCompositionStart}
          onCompositionEnd={handleCompositionEnd}
          onPointerDownCapture={handleSearchPointerDown}
          onPointerUpCapture={handleSearchPointerUp}
          onPointerCancelCapture={handleSearchPointerCancel}
          onClickCapture={handleSearchClickCapture}
          enterKeyHint="search"
          style={st.searchBar}
        />
        {!isMobile && showTargetTabs && tabs}
        <div ref={resultsRef} id="search-results" role="region" aria-label={t('search.results')} data-testid="search-results-panel" style={st.results} aria-live="polite">
          <SearchResultsPanel
            activeTarget={effectiveActiveTarget}
            visibleTargets={visibleTargets}
            query={query}
            search={search}
            styles={st}
            resultsRef={resultsRef}
            keyboardInset={keyboardInset}
            isMobile={isMobile}
            t={t}
            onSongNavigateStart={onClose}
            onPlayerSelect={handlePlayerSelect}
            onBandSelect={handleBandSelect}
          />
        </div>
        {isMobile && showTargetTabs && tabs}
      </div>
    </ModalShell>
  );
}

function SearchTargetTabButton({
  target,
  label,
  selected,
  style,
  onToggle,
}: {
  target: SearchTarget;
  label: string;
  selected: boolean;
  style: CSSProperties;
  onToggle: (target: SearchTarget) => void;
}) {
  const pressHandlers = usePressAction<HTMLButtonElement>({ onPress: () => onToggle(target) });

  return (
    <button
      type="button"
      aria-pressed={selected}
      aria-controls="search-results"
      data-testid={`search-target-filter-${target}`}
      style={style}
      {...pressHandlers}
    >
      {label}
    </button>
  );
}

interface RenderResultsArgs {
  activeTarget: SearchTarget | null;
  visibleTargets: readonly SearchTarget[];
  query: string;
  search: ReturnType<typeof useUnifiedSearch>;
  styles: ReturnType<typeof useStyles>;
  resultsRef: React.RefObject<HTMLDivElement | null>;
  keyboardInset: number;
  isMobile: boolean;
  t: ReturnType<typeof useTranslation>['t'];
  onSongNavigateStart: () => void;
  onPlayerSelect: (player: AccountSearchResult) => void;
  onBandSelect: (band: BandSearchResult) => void;
}

function SearchResultsPanel(props: RenderResultsArgs) {
  const { activeTarget, visibleTargets, query, search, styles: st, resultsRef, keyboardInset } = props;
  const resultListRef = useRef<HTMLDivElement>(null);
  const trimmedQuery = query.trim();
  const isShort = trimmedQuery.length < 2;
  const waitingForDebounce = !isShort && trimmedQuery !== search.debouncedQuery;
  const loading = !isShort && (waitingForDebounce || search.debouncing || (activeTarget == null
    ? visibleTargets.some(target => search.loading[target])
    : search.loading[activeTarget]));
  const centeredMessage = !loading && shouldUseCenteredResultList(activeTarget, visibleTargets, trimmedQuery, search);
  const contentSignature = useMemo(
    () => getContentSignature(activeTarget, visibleTargets, trimmedQuery, search),
    [activeTarget, search, trimmedQuery, visibleTargets],
  );
  const [loadPhase, setLoadPhase] = useState<LoadPhase>(LoadPhase.ContentIn);
  const [staggerSignature, setStaggerSignature] = useState<string | null>(null);
  const loadPhaseRef = useRef(loadPhase);
  const staggeredSignaturesRef = useRef<Partial<Record<SearchViewKey, string>>>({});
  const previousQueryRef = useRef(trimmedQuery);
  const updateScrollMask = useScrollMask(resultsRef, [activeTarget, contentSignature, loadPhase, keyboardInset], { selfScroll: true, size: SEARCH_SCROLL_FADE_SIZE });
  const updateScrollFade = useScrollFade(resultsRef, resultListRef, [activeTarget, contentSignature, loadPhase, keyboardInset], { distance: SEARCH_SCROLL_FADE_SIZE });
  const { resetRush } = useStaggerRush(resultListRef, resultsRef);
  loadPhaseRef.current = loadPhase;

  useEffect(() => {
    if (previousQueryRef.current === trimmedQuery) return;
    previousQueryRef.current = trimmedQuery;
    staggeredSignaturesRef.current = {};
    setStaggerSignature(null);
    resetRush();
  }, [resetRush, trimmedQuery]);

  useEffect(() => {
    resetRush();
  }, [contentSignature, resetRush]);

  useEffect(() => {
    updateScrollMask();
    updateScrollFade();
  }, [contentSignature, loadPhase, updateScrollFade, updateScrollMask]);

  useEffect(() => {
    updateScrollMask();
    updateScrollFade();

    const rafId = requestAnimationFrame(() => {
      updateScrollMask();
      updateScrollFade();
    });
    const timeoutId = window.setTimeout(() => {
      updateScrollMask();
      updateScrollFade();
    }, QUICK_FADE_MS + 20);

    return () => {
      cancelAnimationFrame(rafId);
      window.clearTimeout(timeoutId);
    };
  }, [keyboardInset, updateScrollFade, updateScrollMask]);

  useEffect(() => {
    if (isShort) {
      setStaggerSignature(null);
      setLoadPhase(LoadPhase.ContentIn);
      return undefined;
    }

    if (loading) {
      setStaggerSignature(null);
      setLoadPhase(LoadPhase.Loading);
      return undefined;
    }

    const viewKey = activeTarget ?? 'global';

    if (staggeredSignaturesRef.current[viewKey] === contentSignature) {
      setStaggerSignature(null);
      setLoadPhase(LoadPhase.ContentIn);
      return undefined;
    }

    if (loadPhaseRef.current === LoadPhase.Loading || loadPhaseRef.current === LoadPhase.SpinnerOut) {
      setLoadPhase(LoadPhase.SpinnerOut);
      const id = setTimeout(() => {
        staggeredSignaturesRef.current[viewKey] = contentSignature;
        setStaggerSignature(contentSignature);
        setLoadPhase(LoadPhase.ContentIn);
      }, SPINNER_FADE_MS);
      return () => clearTimeout(id);
    }

    staggeredSignaturesRef.current[viewKey] = contentSignature;
    setStaggerSignature(contentSignature);
    setLoadPhase(LoadPhase.ContentIn);
    return undefined;
  }, [activeTarget, contentSignature, isShort, loading, search.debouncedQuery]);

  if ((loading || loadPhase !== LoadPhase.ContentIn) && !isShort) {
    const spinnerStyle = !loading && loadPhase === LoadPhase.SpinnerOut ? st.spinnerWrapOut : st.spinnerWrapIn;
    return <div style={spinnerStyle}><ArcSpinner size={SpinnerSize.MD} /></div>;
  }

  return (
    <div ref={resultListRef} style={centeredMessage ? st.centeredResultList : activeTarget == null ? st.globalResultList : st.resultList} data-testid="search-result-list">
      {renderResults({ ...props, shouldStagger: staggerSignature === contentSignature && loadPhase === LoadPhase.ContentIn })}
    </div>
  );
}

function shouldUseCenteredResultList(activeTarget: SearchTarget | null, visibleTargets: readonly SearchTarget[], query: string, search: ReturnType<typeof useUnifiedSearch>): boolean {
  if (query.length < 2) return true;
  if (activeTarget == null) return visibleTargets.every(target => !shouldRenderGlobalSection(target, search));
  return !!search.errors[activeTarget] || getTargetResultCount(activeTarget, search) === 0;
}

function getContentSignature(activeTarget: SearchTarget | null, visibleTargets: readonly SearchTarget[], query: string, search: ReturnType<typeof useUnifiedSearch>): string {
  const viewKey = activeTarget ?? 'global';
  if (query.length < 2) return `${viewKey}:short`;
  const debouncedQuery = search.debouncedQuery || query;
  if (activeTarget == null) {
    return `global:${visibleTargets.map(target => getTargetContentSignature(target, debouncedQuery, search)).join('||')}`;
  }

  return getTargetContentSignature(activeTarget, debouncedQuery, search);
}

function getTargetContentSignature(target: SearchTarget, debouncedQuery: string, search: ReturnType<typeof useUnifiedSearch>): string {
  if (search.errors[target]) return `${target}:${debouncedQuery}:error`;

  if (target === 'songs') {
    return `${target}:${debouncedQuery}:${search.songResults.map(song => song.songId).join('|')}`;
  }

  if (target === 'players') {
    return `${target}:${debouncedQuery}:${search.playerResults.map(player => player.accountId).join('|')}`;
  }

  return `${target}:${debouncedQuery}:${search.bandResults.map(band => `${band.bandId}:${band.teamKey}`).join('|')}`;
}

function getStaggerStyle(index: number, enabled: boolean, maxItems = SEARCH_STAGGER_VISIBLE_ITEMS): CSSProperties | undefined {
  if (!enabled) return undefined;
  const delay = staggerDelay(index, STAGGER_INTERVAL, maxItems);
  return delay == null ? undefined : { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out ${delay}ms forwards` };
}

function renderResults(args: RenderResultsArgs & { shouldStagger: boolean }) {
  const { activeTarget, visibleTargets, query, styles: st, t } = args;
  const isShort = query.trim().length < 2;

  if (isShort) {
    return <div style={st.hintCenter}>{t('search.enterQuery')}</div>;
  }

  if (activeTarget == null) {
    let staggerIndex = 0;
    const renderedTargets = visibleTargets.filter(target => shouldRenderGlobalSection(target, args.search));

    if (renderedTargets.length === 0) {
      return <div style={st.hintCenter}>{t('search.noResults.all')}</div>;
    }

    return renderedTargets.map((target, sectionIndex) => {
      const headingIndex = staggerIndex;
      const contentStartIndex = headingIndex + 1;
      const sectionId = `search-section-${target}`;
      const headingId = `${sectionId}-heading`;
      const section = (
        <section key={target} id={sectionId} data-testid={sectionId} style={sectionIndex === 0 ? st.section : st.sectionSpaced} aria-labelledby={headingId}>
          <h3 id={headingId} style={{ ...st.sectionHeading, ...getStaggerStyle(headingIndex, args.shouldStagger, Number.POSITIVE_INFINITY) }}>{t(SEARCH_TARGET_LABEL_KEYS[target])}</h3>
          <div style={st.sectionList}>
            {renderTargetResults({ ...args, activeTarget: target, startIndex: contentStartIndex, compactEmpty: true })}
          </div>
        </section>
      );
      staggerIndex += 1 + getTargetStaggerSlotCount(target, args.search);
      return section;
    });
  }

  return renderTargetResults({ ...args, activeTarget, startIndex: 0, compactEmpty: false });
}

function shouldRenderGlobalSection(target: SearchTarget, search: ReturnType<typeof useUnifiedSearch>): boolean {
  if (search.errors[target]) return true;
  return getTargetResultCount(target, search) > 0;
}

function getTargetResultCount(target: SearchTarget, search: ReturnType<typeof useUnifiedSearch>): number {
  if (target === 'songs') return search.songResults.length;
  if (target === 'players') return search.playerResults.length;
  return search.bandResults.length;
}

function renderTargetResults({ activeTarget, search, styles: st, isMobile, t, onSongNavigateStart, onPlayerSelect, onBandSelect, shouldStagger, startIndex, compactEmpty }: RenderResultsArgs & { activeTarget: SearchTarget; shouldStagger: boolean; startIndex: number; compactEmpty: boolean }) {
  const hasError = search.errors[activeTarget];
  const emptyStyle = compactEmpty ? st.sectionHint : st.hintCenter;

  if (hasError) {
    return <div style={{ ...emptyStyle, ...getStaggerStyle(startIndex, shouldStagger) }}>{t('search.failed')}</div>;
  }

  if (activeTarget === 'songs') {
    if (search.songResults.length === 0) return <div style={{ ...emptyStyle, ...getStaggerStyle(startIndex, shouldStagger) }}>{t('search.noResults.songs')}</div>;
    return search.songResults.map((song, index) => (
      <div
        key={song.songId}
        style={{ ...st.songRowWrap, ...getStaggerStyle(startIndex + index, shouldStagger) }}
      >
        <SongRow
          song={song}
          instrument={DEFAULT_INSTRUMENT}
          instrumentFilter={null}
          showInstrumentIcons={false}
          enabledInstruments={EMPTY_INSTRUMENTS}
          metadataOrder={EMPTY_METADATA_ORDER}
          sortMode="title"
          isMobile={isMobile}
          onBeforeInternalNavigate={onSongNavigateStart}
        />
      </div>
    ));
  }

  if (activeTarget === 'players') {
    if (search.playerResults.length === 0) return <div style={{ ...emptyStyle, ...getStaggerStyle(startIndex, shouldStagger) }}>{t('search.noResults.players')}</div>;
    return search.playerResults.map((player, index) => (
      <SearchPlayerResultButton
        key={player.accountId}
        player={player}
        style={{ ...st.resultBtn, ...getStaggerStyle(startIndex + index, shouldStagger) }}
        titleStyle={st.resultTitle}
        onSelect={onPlayerSelect}
      />
    ));
  }

  if (search.bandResults.length === 0) return <div style={{ ...emptyStyle, ...getStaggerStyle(startIndex, shouldStagger) }}>{t('search.noResults.bands')}</div>;
  return search.bandResults.map((band, index) => {
    const members = band.members.map(member => member.displayName ?? member.accountId).filter(Boolean).join(' · ');
    const cardEntry = toPlayerBandEntry(band);
    return (
      <div
        key={band.bandId}
        style={{ ...st.bandCardWrap, ...getStaggerStyle(startIndex + index, shouldStagger) }}
      >
        <PlayerBandCard
          entry={cardEntry}
          ariaLabel={members || t('search.unknownBand')}
          appearanceLabel={t('bandList.appearanceLabel', { count: band.appearanceCount })}
          onPress={() => onBandSelect(band)}
        />
      </div>
    );
  });
}

function SearchPlayerResultButton({
  player,
  style,
  titleStyle,
  onSelect,
}: {
  player: AccountSearchResult;
  style: CSSProperties;
  titleStyle: CSSProperties;
  onSelect: (player: AccountSearchResult) => void;
}) {
  const pressHandlers = usePressAction<HTMLButtonElement>({ onPress: () => onSelect(player) });

  return (
    <button key={player.accountId} type="button" data-testid="search-player-result" style={style} {...pressHandlers}>
      <span style={titleStyle}>{player.displayName}</span>
    </button>
  );
}

function getTargetStaggerSlotCount(target: SearchTarget, search: ReturnType<typeof useUnifiedSearch>): number {
  if (search.errors[target]) return 1;
  if (target === 'songs') return Math.max(1, search.songResults.length);
  if (target === 'players') return Math.max(1, search.playerResults.length);
  return Math.max(1, search.bandResults.length);
}

function toPlayerBandEntry(band: BandSearchResult): PlayerBandEntry {
  return {
    bandId: band.bandId,
    teamKey: band.teamKey,
    bandType: band.bandType,
    appearanceCount: band.appearanceCount,
    members: band.members,
  };
}

function useStyles(isMobile: boolean, keyboardInset: number) {
  return useMemo(() => {
    const selectorPad = padding(Gap.lg, Gap.md + 4);
    const bodyPadding = isMobile
      ? paddingWithSafeAreaBottom(Gap.sm, Gap.section, Gap.section)
      : padding(Gap.sm, Gap.section, Gap.section);
    const keyboardTransform = isMobile && keyboardInset > 0 ? `translate3d(0, -${keyboardInset}px, 0)` : undefined;
    const keyboardResultsMargin = isMobile && keyboardInset > 0 ? keyboardInset : 0;
    const tabBase: CSSProperties = {
      ...frostedCard,
      flex: 1,
      minHeight: 44,
      padding: selectorPad,
      borderRadius: Radius.md,
      color: Colors.textSecondary,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      cursor: Cursor.pointer,
      textAlign: TextAlign.center,
    };

    return {
      body: {
        flex: 1,
        ...flexColumn,
        padding: bodyPadding,
        gap: Gap.md,
        overflow: Overflow.hidden,
      } as CSSProperties,
      searchBar: {
        display: Display.flex,
        alignItems: Align.center,
        gap: Gap.sm,
        height: 48,
        padding: padding(0, Gap.xl),
        boxSizing: BoxSizing.borderBox,
        borderRadius: Radius.full,
        border: `${Border.thin}px solid ${Colors.borderPrimary}`,
        backgroundColor: Colors.backgroundCard,
        cursor: Cursor.text,
        flexShrink: 0,
      } as CSSProperties,
      tabs: {
        display: Display.flex,
        gap: Gap.sm,
        flexShrink: 0,
        transform: keyboardTransform,
      } as CSSProperties,
      tab: tabBase,
      tabSelected: {
        flex: 1,
        minHeight: 44,
        padding: selectorPad,
        borderRadius: Radius.md,
        fontSize: Font.md,
        fontWeight: Weight.semibold,
        cursor: Cursor.pointer,
        textAlign: TextAlign.center,
        color: Colors.textPrimary,
        backgroundColor: Colors.purpleHighlight,
        backgroundImage: CssValue.none,
        border: border(1, Colors.purpleHighlightBorder),
        boxShadow: Shadow.frostedActive,
      } as CSSProperties,
      results: {
        flex: 1,
        minHeight: 0,
        ...flexColumn,
        overflowY: Overflow.auto,
        marginBottom: keyboardResultsMargin,
      } as CSSProperties,
      resultList: {
        ...flexColumn,
        gap: Gap.xs,
        flexGrow: 1,
        flexShrink: 0,
        minHeight: CssValue.full,
        boxSizing: BoxSizing.borderBox,
        paddingTop: SEARCH_SCROLL_FADE_SIZE,
        paddingBottom: SEARCH_SCROLL_FADE_SIZE,
      } as CSSProperties,
      globalResultList: {
        ...flexColumn,
        gap: Gap.md,
        flexGrow: 1,
        flexShrink: 0,
        minHeight: CssValue.full,
        boxSizing: BoxSizing.borderBox,
        paddingTop: SEARCH_SCROLL_FADE_SIZE,
        paddingBottom: SEARCH_SCROLL_FADE_SIZE,
      } as CSSProperties,
      centeredResultList: {
        ...flexColumn,
        flex: 1,
        minHeight: 0,
        boxSizing: BoxSizing.borderBox,
      } as CSSProperties,
      section: {
        ...flexColumn,
        gap: Gap.sm,
        minWidth: 0,
      } as CSSProperties,
      sectionSpaced: {
        ...flexColumn,
        gap: Gap.sm,
        minWidth: 0,
        marginTop: Gap.md,
      } as CSSProperties,
      sectionHeading: {
        color: 'rgba(255, 255, 255, 0.74)',
        fontSize: Font.xs,
        fontWeight: Weight.semibold,
        lineHeight: LineHeight.snug,
        letterSpacing: 0,
        textTransform: TextTransform.uppercase,
        padding: padding(0, Gap.xs),
        margin: 0,
      } as CSSProperties,
      sectionList: {
        ...flexColumn,
        gap: Gap.xs,
        minWidth: 0,
      } as CSSProperties,
      sectionHint: {
        ...frostedCard,
        minHeight: 54,
        ...flexCenter,
        flexShrink: 0,
        padding: padding(Gap.md, Gap.section),
        borderRadius: Radius.md,
        color: Colors.textTertiary,
        fontSize: Font.sm,
        lineHeight: LineHeight.relaxed,
        textAlign: TextAlign.center,
      } as CSSProperties,
      spinnerWrapIn: {
        ...flexCenter,
        flex: 1,
        animation: `fadeIn ${SPINNER_FADE_MS}ms ease-out forwards`,
      } as CSSProperties,
      spinnerWrapOut: {
        ...flexCenter,
        flex: 1,
        animation: `fadeOut ${SPINNER_FADE_MS}ms ease-out forwards`,
      } as CSSProperties,
      hintCenter: {
        ...flexCenter,
        flex: 1,
        color: Colors.textTertiary,
        fontSize: isMobile ? Font.md : Font.lg,
        lineHeight: LineHeight.relaxed,
        textAlign: TextAlign.center,
        padding: padding(Gap.section),
      } as CSSProperties,
      resultBtn: {
        ...frostedCard,
        width: CssValue.full,
        flexShrink: 0,
        minHeight: 54,
        ...flexColumn,
        justifyContent: Justify.center,
        gap: 0,
        padding: padding(Gap.md, Gap.section),
        borderRadius: Radius.md,
        color: Colors.textSecondary,
        cursor: Cursor.pointer,
        textAlign: TextAlign.left,
      } as CSSProperties,
      songRowWrap: {
        cursor: Cursor.pointer,
        flexShrink: 0,
      } as CSSProperties,
      bandCardWrap: {
        cursor: Cursor.pointer,
        flexShrink: 0,
      } as CSSProperties,
      resultTitle: {
        color: Colors.textPrimary,
        fontSize: Font.md,
        fontWeight: Weight.semibold,
      } as CSSProperties,
      resultSubtitle: {
        color: Colors.textMuted,
        fontSize: Font.sm,
        lineHeight: LineHeight.relaxed,
      } as CSSProperties,
    };
  }, [isMobile, keyboardInset]);
}
