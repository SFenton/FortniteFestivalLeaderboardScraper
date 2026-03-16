import { useEffect, useLayoutEffect, useState, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { IoSearch, IoClose, IoPerson } from 'react-icons/io5';
import type { TrackedPlayer } from '../../hooks/useTrackedPlayer';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../hooks/useVisualViewport';
import { api } from '../../api/client';
import type { AccountSearchResult } from '../../models';
import { Colors, Font, Gap, Radius } from '@festival/theme';
import css from './MobilePlayerSearchModal.module.css';

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
  visible, onClose, onSelect, player, onDeselect, isMobile, title,
}: Props) {
  const { t } = useTranslation();
  const effectiveTitle = title ?? t('common.selectPlayerProfile');
  const [mounted, setMounted] = useState(false);
  const [animIn, setAnimIn] = useState(false);
  const [contentReady, setContentReady] = useState(false);
  const [dismissing, setDismissing] = useState(false);
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<AccountSearchResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [showSpinner, setShowSpinner] = useState(false);
  const [spinnerOpacity, setSpinnerOpacity] = useState(0);
  const [resultsReady, setResultsReady] = useState(false);
  const [resultSeq, setResultSeq] = useState(0);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const vvHeight = useVisualViewportHeight();
  const vvOffsetTop = useVisualViewportOffsetTop();

  useEffect(() => { if (visible) setMounted(true); else setAnimIn(false); }, [visible]);

  useLayoutEffect(() => {
    if (mounted && visible) {
      const id = requestAnimationFrame(() => setAnimIn(true));
      setTimeout(() => inputRef.current?.focus(), MODAL_TRANSITION_MS);
      return () => cancelAnimationFrame(id);
    }
  }, [mounted, visible]);

  const handleTransitionEnd = useCallback(() => {
    if (animIn) { setContentReady(true); }
    else { setMounted(false); setContentReady(false); setDismissing(false); setQuery(''); setResults([]); setShowSpinner(false); setSpinnerOpacity(0); setResultsReady(false); }
  }, [animIn]);

  useEffect(() => {
    if (!mounted) return;
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [mounted, onClose]);

  useEffect(() => {
    if (loading) { setResultsReady(false); setShowSpinner(true); requestAnimationFrame(() => requestAnimationFrame(() => setSpinnerOpacity(1))); }
    else if (showSpinner) { setSpinnerOpacity(0); }
  }, [loading]); // eslint-disable-line react-hooks/exhaustive-deps

  const handleSpinnerTransitionEnd = useCallback(() => {
    if (spinnerOpacity === 0 && !loading) { setShowSpinner(false); setResultsReady(true); }
  }, [spinnerOpacity, loading]);

  const search = useCallback(async (q: string) => {
    if (q.length < 2) { setResults([]); return; }
    setLoading(true); setResults([]);
    try { const res = await api.searchAccounts(q, 10); setResults(res.results); setResultSeq(s => s + 1); }
    catch { setResults([]); }
    finally { setLoading(false); }
  }, []);

  const handleChange = (value: string) => {
    setQuery(value);
    if (value.trim().length < 2) { if (debounceRef.current) clearTimeout(debounceRef.current); setResults([]); setLoading(false); setShowSpinner(false); setSpinnerOpacity(0); setResultsReady(true); return; }
    if (debounceRef.current) clearTimeout(debounceRef.current);
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

  if (!mounted) return null;

  return (
    <>
      <div
        style={{ position: 'fixed', inset: 0, backgroundColor: Colors.overlayModal, zIndex: 1000, opacity: animIn ? 1 : 0, transition: `opacity ${MODAL_TRANSITION_MS}ms ease` }}
        onClick={onClose}
      />
      <div
        role="dialog" aria-modal="true" aria-label="Select Player"
        style={{
          position: 'fixed',
          ...(isMobile
            ? { left: 0, right: 0, top: vvOffsetTop + vvHeight * 0.2, height: vvHeight * 0.8, borderTopLeftRadius: Radius.lg, borderTopRightRadius: Radius.lg }
            : { top: '50%', left: '50%', width: 420, height: 600, maxHeight: '90vh', borderRadius: Radius.lg, transform: animIn ? 'translate(-50%, -50%)' : 'translate(-50%, -40%)', opacity: animIn ? 1 : 0 }),
          zIndex: 1001, display: 'flex', flexDirection: 'column' as const,
          backgroundColor: Colors.surfaceFrosted, backdropFilter: 'blur(18px)', WebkitBackdropFilter: 'blur(18px)', color: Colors.textPrimary,
          ...(isMobile ? { transform: animIn ? 'translateY(0)' : 'translateY(100%)' } : {}),
          transition: isMobile ? `transform ${MODAL_TRANSITION_MS}ms ease` : `opacity ${MODAL_TRANSITION_MS}ms ease, transform ${MODAL_TRANSITION_MS}ms ease`,
        }}
        onTransitionEnd={handleTransitionEnd}
      >
        <div className={css.header}><h2 className={css.title}>{effectiveTitle}</h2><button className={css.closeBtn} onClick={onClose} aria-label={t('common.dismiss')}><IoClose size={18} /></button></div>
        <div className={css.body}>
          {player && (
            <div className={css.playerCard}>
              <span style={{ ...styles.profileCircleLg, ...stagger(dismissing ? 450 : 0) }}><IoPerson size={32} /></span>
              <span style={{ ...styles.playerName, ...stagger(dismissing ? 300 : 150) }}>{player.displayName}</span>
              <span style={{ ...styles.deselectHint, ...stagger(dismissing ? 150 : 300) }}>{t('common.deselectHint')}</span>
              <button style={{ ...styles.deselectBtn, ...stagger(dismissing ? 0 : 450), ...(dismissing ? { pointerEvents: 'none' as const } : {}) }} onClick={handleDeselect}>{t('common.deselectPlayer')}</button>
            </div>
          )}
          {!player && (
            <>
              <div style={{ ...styles.searchPill, ...stagger(0) }} onClick={e => { e.currentTarget.querySelector('input')?.focus(); }}>
                <IoSearch size={16} style={{ color: Colors.textTertiary, flexShrink: 0 }} />
                <input ref={inputRef} className={css.searchInput} placeholder="Search player…" value={query} onChange={e => handleChange(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }} enterKeyHint="done" />
              </div>
              <div style={{ ...styles.results, ...stagger(150) }}>
                {showSpinner && (<div style={{ ...styles.spinnerWrap, opacity: spinnerOpacity, transition: 'opacity 250ms ease' }} onTransitionEnd={handleSpinnerTransitionEnd}><div className={css.arcSpinner} /></div>)}
                {!showSpinner && !loading && query.length < 2 && (<div className={css.hintCenter}>{t('common.enterUsername')}</div>)}
                {!showSpinner && !loading && query.length >= 2 && results.length === 0 && (<div className={css.hintCenter}>{t('common.noMatchingUsername')}</div>)}
                {!showSpinner && resultsReady && results.map((r, i) => (
                  <button key={`${resultSeq}-${r.accountId}`} style={{ ...css.resultBtn, opacity: 0, animation: `fadeInUp 300ms ease-out ${i * 50}ms forwards` }} onClick={() => handleSelect(r)}>{r.displayName}</button>
                ))}
              </div>
            </>
          )}
        </div>
      </div>
    </>
  );
}

