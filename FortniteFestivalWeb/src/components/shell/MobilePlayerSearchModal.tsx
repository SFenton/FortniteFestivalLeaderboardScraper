import { useEffect, useLayoutEffect, useState, useCallback, useRef } from 'react';
import { IoSearch, IoClose, IoPerson } from 'react-icons/io5';
import type { TrackedPlayer } from '../../hooks/useTrackedPlayer';
import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../hooks/useVisualViewport';
import { api } from '../../api/client';
import type { AccountSearchResult } from '../../models';
import { Colors, Font, Gap, Radius } from '@festival/theme';

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
  visible, onClose, onSelect, player, onDeselect, isMobile, title = 'Select Player Profile',
}: Props) {
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
        <div style={styles.header}><h2 style={styles.title}>{title}</h2><button style={styles.closeBtn} onClick={onClose} aria-label="Close"><IoClose size={18} /></button></div>
        <div style={styles.body}>
          {player && (
            <div style={styles.playerCard}>
              <span style={{ ...styles.profileCircleLg, ...stagger(dismissing ? 450 : 0) }}><IoPerson size={32} /></span>
              <span style={{ ...styles.playerName, ...stagger(dismissing ? 300 : 150) }}>{player.displayName}</span>
              <span style={{ ...styles.deselectHint, ...stagger(dismissing ? 150 : 300) }}>Deselecting will hide suggestions, statistics, and per-song scores from the song list.</span>
              <button style={{ ...styles.deselectBtn, ...stagger(dismissing ? 0 : 450), ...(dismissing ? { pointerEvents: 'none' as const } : {}) }} onClick={handleDeselect}>Deselect Player</button>
            </div>
          )}
          {!player && (
            <>
              <div style={{ ...styles.searchPill, ...stagger(0) }} onClick={e => { e.currentTarget.querySelector('input')?.focus(); }}>
                <IoSearch size={16} style={{ color: Colors.textTertiary, flexShrink: 0 }} />
                <input ref={inputRef} style={styles.searchInput} placeholder="Search player…" value={query} onChange={e => handleChange(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }} enterKeyHint="done" />
              </div>
              <div style={{ ...styles.results, ...stagger(150) }}>
                {showSpinner && (<div style={{ ...styles.spinnerWrap, opacity: spinnerOpacity, transition: 'opacity 250ms ease' }} onTransitionEnd={handleSpinnerTransitionEnd}><div style={styles.arcSpinner} /></div>)}
                {!showSpinner && !loading && query.length < 2 && (<div style={styles.hintCenter}>Enter a username to search for.</div>)}
                {!showSpinner && !loading && query.length >= 2 && results.length === 0 && (<div style={styles.hintCenter}>No matching username found.</div>)}
                {!showSpinner && resultsReady && results.map((r, i) => (
                  <button key={`${resultSeq}-${r.accountId}`} style={{ ...styles.resultBtn, opacity: 0, animation: `fadeInUp 300ms ease-out ${i * 50}ms forwards` }} onClick={() => handleSelect(r)}>{r.displayName}</button>
                ))}
              </div>
            </>
          )}
        </div>
      </div>
    </>
  );
}

const styles: Record<string, React.CSSProperties> = {
  header: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: `${Gap.xl}px 16px ${Gap.xl}px ${Gap.section}px`, flexShrink: 0 },
  title: { fontSize: Font.xl, fontWeight: 700, margin: 0 },
  closeBtn: { width: 32, height: 32, borderRadius: '50%', background: Colors.surfaceElevated, border: `1px solid ${Colors.borderPrimary}`, color: Colors.textSecondary, display: 'flex', alignItems: 'center', justifyContent: 'center', cursor: 'pointer', flexShrink: 0 },
  body: { flex: 1, display: 'flex', flexDirection: 'column' as const, padding: `${Gap.sm}px ${Gap.section}px ${Gap.section}px`, gap: Gap.md, overflow: 'hidden' },
  playerCard: { display: 'flex', flexDirection: 'column' as const, alignItems: 'center', gap: Gap.xl, padding: `${Gap.section * 2}px ${Gap.section}px`, flex: 1, justifyContent: 'center' },
  profileCircleLg: { display: 'flex', alignItems: 'center', justifyContent: 'center', width: 64, height: 64, borderRadius: '50%', backgroundColor: Colors.surfaceSubtle, border: `1px solid ${Colors.borderSubtle}`, flexShrink: 0 },
  playerName: { fontSize: Font.xl, fontWeight: 700, color: Colors.textPrimary },
  deselectHint: { fontSize: Font.sm, color: Colors.textTertiary, textAlign: 'center' as const, lineHeight: '1.5' },
  deselectBtn: { background: Colors.dangerBg, border: `1px solid ${Colors.statusRed}`, borderRadius: Radius.xs, color: Colors.textPrimary, fontSize: Font.sm, fontWeight: 600, cursor: 'pointer', padding: `${Gap.sm}px ${Gap.xl}px`, whiteSpace: 'nowrap' as const },
  searchPill: { display: 'flex', alignItems: 'center', gap: Gap.sm, height: 48, padding: `0 ${Gap.xl}px`, boxSizing: 'border-box' as const, borderRadius: Radius.full, border: `1px solid ${Colors.borderPrimary}`, backgroundColor: Colors.backgroundCard, cursor: 'text', flexShrink: 0 },
  searchInput: { flex: 1, background: 'none', border: 'none', outline: 'none', color: Colors.textPrimary, fontSize: Font.md },
  results: { flex: 1, overflowY: 'auto' as const, display: 'flex', flexDirection: 'column' as const, gap: Gap.xs },
  spinnerWrap: { display: 'flex', alignItems: 'center', justifyContent: 'center', flex: 1 },
  hintCenter: { display: 'flex', alignItems: 'center', justifyContent: 'center', flex: 1, color: Colors.textTertiary, fontSize: Font.lg, textAlign: 'center' as const },
  arcSpinner: { width: 36, height: 36, border: '3px solid rgba(255,255,255,0.10)', borderTopColor: Colors.accentPurple, borderRadius: '50%', animation: 'spin 0.8s linear infinite' },
  resultBtn: { display: 'block', width: '100%', padding: `${Gap.xl}px ${Gap.section}px`, background: 'none', border: 'none', borderRadius: Radius.xs, color: Colors.textSecondary, fontSize: Font.md, cursor: 'pointer', textAlign: 'left' as const },
};
