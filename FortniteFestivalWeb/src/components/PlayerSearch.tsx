import { useCallback } from 'react';
import { Link } from 'react-router-dom';
import type { TrackedPlayer } from '../hooks/useTrackedPlayer';
import { useAccountSearch } from '../hooks/useAccountSearch';
import css from './PlayerSearch.module.css';

type Props = {
  player: TrackedPlayer | null;
  onSelect: (player: TrackedPlayer) => void;
  onClear: () => void;
  isSyncing?: boolean;
};

export default function PlayerSearch({ player, onSelect, onClear, isSyncing }: Props) {
  if (player) {
    return (
      <div className={css.selectedContainer}>
        {isSyncing && <span className={css.headerSpinner} />}
        <Link to="/statistics" className={css.selectedName}>
          {player.displayName}
        </Link>
        <button className={css.clearButton} onClick={onClear} title="Stop tracking">
          ✕
        </button>
      </div>
    );
  }

  return <SearchInput onSelect={onSelect} />;
}

function SearchInput({ onSelect }: { onSelect: (p: TrackedPlayer) => void }) {
  const handleSelect = useCallback(
    (r: { accountId: string; displayName: string }) =>
      onSelect({ accountId: r.accountId, displayName: r.displayName }),
    [onSelect],
  );
  const s = useAccountSearch(handleSelect);

  return (
    <div ref={s.containerRef} className={css.searchContainer}>
      <input
        className={css.searchInput}
        placeholder="Search player…"
        value={s.query}
        onChange={(e) => s.handleChange(e.target.value)}
        onKeyDown={s.handleKeyDown}
        onFocus={() => { if (s.results.length > 0) s.close(); }}
      />
      {s.loading && <span className={css.spinner} />}
      {s.isOpen && (
        <div className={css.dropdown}>
          {s.results.map((r, i) => (
            <button
              key={r.accountId}
              className={i === s.activeIndex ? css.dropdownItemActive : css.dropdownItem}
              onMouseEnter={() => s.setActiveIndex(i)}
              onClick={() => s.selectResult(r)}
            >
              {r.displayName}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
