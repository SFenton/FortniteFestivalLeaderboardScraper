/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { IoPerson } from 'react-icons/io5';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import { api } from '../../../api/client';
import type { AccountSearchResult } from '@festival/core/api/serverTypes';
import SearchBar, { type SearchBarRef } from '../../common/SearchBar';
import ArcSpinner from '../../common/ArcSpinner';
import { useFadeSpinner } from '../../../hooks/ui/useFadeSpinner';
import ModalShell from '../../modals/components/ModalShell';
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
  visible, onClose, onSelect, player, onDeselect, isMobile: _isMobile, title,
}: Props) {
  const { t } = useTranslation();
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
      desktopClassName={css.panelDesktop}
      transitionMs={MODAL_TRANSITION_MS}
      onOpenComplete={handleOpenComplete}
      onCloseComplete={handleCloseComplete}
    >
      <div className={css.body}>
        {player && (
          <div className={css.playerCard}>
            <span className={css.profileCircleLg} style={stagger(dismissing ? 450 : 0)}><IoPerson size={32} /></span>
            <span className={css.playerName} style={stagger(dismissing ? 300 : 150)}>{player.displayName}</span>
            <span className={css.deselectHint} style={stagger(dismissing ? 150 : 300)}>{t('common.deselectHint')}</span>
            <button className={css.deselectBtn} style={{ ...stagger(dismissing ? 0 : 450), ...(dismissing ? { pointerEvents: 'none' as const } : {}) }} onClick={handleDeselect}>{t('common.deselectPlayer')}</button>
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
              className={css.searchPill}
              style={stagger(0)}
            />
            <div className={css.results} style={stagger(150)}>
              {spinner.visible && <div className={css.spinnerWrap} style={{ opacity: spinner.opacity, transition: 'opacity 250ms ease' }} onTransitionEnd={spinner.onTransitionEnd}><ArcSpinner size="md" /></div>}
              {!spinner.visible && !loading && !debouncing && query.length < 2 && results.length === 0 && (<div className={css.hintCenter}>{t('common.enterUsername')}</div>)}
              {!spinner.visible && !loading && !debouncing && query.length >= 2 && results.length === 0 && (<div className={css.hintCenter}>{t('common.noMatchingUsername')}</div>)}
              {!spinner.visible && !loading && !debouncing && results.length > 0 && results.map((r, i) => (
                <button key={`${resultSeq}-${r.accountId}`} className={css.resultBtn} style={{ opacity: 0, animation: `fadeInUp 300ms ease-out ${i * 50}ms forwards` }} onClick={() => handleSelect(r)}>{r.displayName}</button>
              ))}
            </div>
          </>
        )}
      </div>
    </ModalShell>
  );
}

