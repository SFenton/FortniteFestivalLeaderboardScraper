/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useCallback, useRef, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoPerson } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import { api } from '../../../api/client';
import type { AccountSearchResult } from '@festival/core/api/serverTypes';
import SearchBar, { type SearchBarRef } from '../../common/SearchBar';
import ArcSpinner, { SpinnerSize } from '../../common/ArcSpinner';
import { useFadeSpinner } from '../../../hooks/ui/useFadeSpinner';
import ModalShell from '../../modals/components/ModalShell';
import {
  Gap, Radius, Font, Weight, Colors, Layout, Display, Align, Justify,
  Overflow, CssValue, TextAlign, LineHeight, Cursor, WhiteSpace, PointerEvents, IconSize, BoxSizing,
  flexColumn, flexCenter,
  btnDanger, padding, Border,
  MODAL_STAGGER_MS,
} from '@festival/theme';

const SEARCH_MODAL_DESKTOP: React.CSSProperties = { width: 420, height: 600, maxHeight: '90vh' };

const searchPill: CSSProperties = {
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
};

const MODAL_TRANSITION_MS = 250;

interface Props {
  visible: boolean;
  onClose: () => void;
  onSelect: (p: TrackedPlayer) => void;
  player: TrackedPlayer | null;
  onDeselect: () => void;
  isMobile: boolean;
  title?: string;
}

export default function MobilePlayerSearchModal({
  visible, onClose, onSelect, player, onDeselect, isMobile: _isMobile, title,
}: Props) {
  const { t } = useTranslation();
  const st = useModalSearchStyles();
  const effectiveTitle = title ?? t('common.selectPlayerProfile');
  const [contentReady, setContentReady] = useState(false);
  const [dismissing, setDismissing] = useState(false);
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<AccountSearchResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [debouncing, setDebouncing] = useState(false);
  const spinner = useFadeSpinner(loading || debouncing);
  const [resultSeq, setResultSeq] = useState(0);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const inputRef = useRef<SearchBarRef>(null);

  const handleOpenComplete = useCallback(() => {
    setContentReady(true);
    setTimeout(() => inputRef.current?.focus(), 50);
  }, []);

  const handleCloseComplete = useCallback(() => {
    setContentReady(false);
    setDismissing(false);
    setQuery('');
    setResults([]);
    setLoading(false);
    setDebouncing(false);
    spinner.reset();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- mount-only close handler
    }, []);

  const search = useCallback(async (q: string) => {
    if (q.length < 2) { setResults([]); return; }
    setDebouncing(false); setLoading(true); setResults([]);
    try { const res = await api.searchAccounts(q, 10); setResults(res.results); setResultSeq(s => s + 1); }
    catch { setResults([]); }
    finally { setLoading(false); }
  }, []);

  const handleChange = (value: string) => {
    setQuery(value);
    if (value.trim().length < 2) { if (debounceRef.current) clearTimeout(debounceRef.current); setResults([]); setLoading(false); setDebouncing(false); spinner.reset(); return; }
    if (debounceRef.current) clearTimeout(debounceRef.current);
    setDebouncing(true);
    debounceRef.current = setTimeout(() => { void search(value.trim()); }, 300);
  };

  const handleSelect = (r: AccountSearchResult) => { onSelect({ accountId: r.accountId, displayName: r.displayName }); onClose(); };

  const handleDeselect = useCallback(() => {
    if (dismissing) return;
    setDismissing(true);
    setTimeout(() => { onDeselect(); setDismissing(false); onClose(); }, 850);
  }, [dismissing, onDeselect, onClose]);

  const stagger = (delayMs: number): React.CSSProperties =>
    dismissing ? { animation: `fadeOutDown 400ms ease-in ${delayMs}ms forwards` }
    : contentReady ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards` }
    : { opacity: 0 };

  return (
    <ModalShell
      visible={visible}
      title={effectiveTitle}
      onClose={onClose}
      desktopStyle={SEARCH_MODAL_DESKTOP}
      transitionMs={MODAL_TRANSITION_MS}
      onOpenComplete={handleOpenComplete}
      onCloseComplete={handleCloseComplete}
    >
      <div style={st.body}>
        {player && (
          <div style={st.playerCard}>
            <span style={{ ...st.profileCircleLg, ...stagger(dismissing ? MODAL_STAGGER_MS * 3 : 0) }}><IoPerson size={IconSize.profile} /></span>
            <span style={{ ...st.playerName, ...stagger(dismissing ? MODAL_STAGGER_MS * 2 : MODAL_STAGGER_MS) }}>{player.displayName}</span>
            <span style={{ ...st.deselectHint, ...stagger(dismissing ? MODAL_STAGGER_MS : MODAL_STAGGER_MS * 2) }}>{t('common.deselectHint')}</span>
            <button style={{ ...st.deselectBtn, ...stagger(dismissing ? 0 : MODAL_STAGGER_MS * 3), ...(dismissing ? { pointerEvents: PointerEvents.none } : {}) }} onClick={handleDeselect}>{t('common.deselectPlayer')}</button>
          </div>
        )}
        {!player && (
          <>
            <SearchBar
              ref={inputRef}
              value={query}
              onChange={handleChange}
              placeholder={t('common.searchPlayer')}
              onKeyDown={e => { if (e.key === 'Enter') inputRef.current?.blur(); }}
              enterKeyHint="done"
              style={{ ...searchPill, ...stagger(0) }}
            />
            <div style={{ ...st.results, ...stagger(150) }}>
              {spinner.visible && <div style={{ ...st.spinnerWrap, opacity: spinner.opacity, transition: 'opacity 250ms ease' }} onTransitionEnd={spinner.onTransitionEnd}><ArcSpinner size={SpinnerSize.MD} /></div>}
              {!spinner.visible && !loading && !debouncing && query.length < 2 && results.length === 0 && (<div style={st.hintCenter}>{t('common.enterUsername')}</div>)}
              {!spinner.visible && !loading && !debouncing && query.length >= 2 && results.length === 0 && (<div style={st.hintCenter}>{t('common.noMatchingUsername')}</div>)}
              {!spinner.visible && !loading && !debouncing && results.length > 0 && results.map((r, i) => (
                <button key={`${resultSeq}-${r.accountId}`} style={{ ...st.resultBtn, opacity: 0, animation: `fadeInUp 300ms ease-out ${i * 50}ms forwards` }} onClick={() => handleSelect(r)}>{r.displayName}</button>
              ))}
            </div>
          </>
        )}
      </div>
    </ModalShell>
  );
}

function useModalSearchStyles() {
  return useMemo(() => ({
    body: {
      flex: 1,
      ...flexColumn,
      padding: padding(Gap.sm, Gap.section, Gap.section),
      gap: Gap.md,
      overflow: Overflow.hidden,
    } as CSSProperties,
    playerCard: {
      ...flexColumn,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(Layout.pillButtonHeight, Gap.section),
      flex: 1,
      justifyContent: Justify.center,
    } as CSSProperties,
    profileCircleLg: {
      display: Display.flex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      width: Layout.profileCircleSize,
      height: Layout.profileCircleSize,
      borderRadius: Radius.full,
      backgroundColor: Colors.surfaceSubtle,
      border: `1px solid ${Colors.borderSubtle}`,
      flexShrink: 0,
    } as CSSProperties,
    playerName: {
      fontSize: Font.xl,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
    } as CSSProperties,
    deselectHint: {
      fontSize: Font.sm,
      color: Colors.textTertiary,
      textAlign: TextAlign.center,
      lineHeight: LineHeight.relaxed,
    } as CSSProperties,
    deselectBtn: {
      ...btnDanger,
      fontSize: Font.sm,
      padding: padding(Gap.sm, Gap.xl),
      whiteSpace: WhiteSpace.nowrap,
    } as CSSProperties,
    results: {
      flex: 1,
      overflowY: Overflow.auto,
      ...flexColumn,
      gap: Gap.xs,
    } as CSSProperties,
    spinnerWrap: {
      ...flexCenter,
      flex: 1,
    } as CSSProperties,
    hintCenter: {
      ...flexCenter,
      flex: 1,
      color: Colors.textTertiary,
      fontSize: Font.lg,
      textAlign: TextAlign.center,
    } as CSSProperties,
    resultBtn: {
      display: Display.block,
      width: CssValue.full,
      padding: padding(Gap.xl, Gap.section),
      background: CssValue.none,
      border: CssValue.none,
      borderRadius: Radius.xs,
      color: Colors.textSecondary,
      fontSize: Font.md,
      cursor: Cursor.pointer,
      textAlign: TextAlign.left,
    } as CSSProperties,
  }), []);
}
