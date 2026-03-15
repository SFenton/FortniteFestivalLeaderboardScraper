import { useRef, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { IoSearch } from 'react-icons/io5';
import { useAccountSearch } from '../../hooks/useAccountSearch';
import { Colors, Font, Gap, Radius, frostedCard } from '@festival/theme';
import type { AccountSearchResult } from '../../models';

/**
 * Desktop header search bar — searches for players by username and navigates
 * to their player page on selection.
 */
export default function HeaderSearch() {
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement>(null);

  const onSelect = useCallback((r: AccountSearchResult) => {
    navigate(`/player/${r.accountId}`);
  }, [navigate]);

  const s = useAccountSearch(onSelect);

  return (
    <div ref={s.containerRef} style={styles.container}>
      <div style={styles.inputWrap} onClick={() => inputRef.current?.focus()}>
        <IoSearch size={16} style={{ color: Colors.textTertiary, flexShrink: 0 }} />
        <input
          ref={inputRef}
          style={styles.input}
          placeholder="Search player…"
          value={s.query}
          onChange={e => s.handleChange(e.target.value)}
          onKeyDown={s.handleKeyDown}
          onFocus={() => { if (s.results.length > 0 && !s.isOpen) s.handleChange(s.query); }}
        />
      </div>
      {s.isOpen && (
        <div style={styles.dropdown}>
          {s.results.map((r, i) => (
            <button
              key={r.accountId}
              style={{
                ...styles.result,
                ...(i === s.activeIndex ? { backgroundColor: Colors.surfaceSubtle } : {}),
              }}
              onClick={() => s.selectResult(r)}
              onMouseEnter={() => s.setActiveIndex(i)}
            >
              {r.displayName}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  container: {
    position: 'relative',
    flex: 1,
    maxWidth: 300,
  },
  inputWrap: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.sm,
    padding: `0 ${Gap.xl}px`,
    height: 36,
    borderRadius: Radius.full,
    ...frostedCard,
    cursor: 'text',
  },
  input: {
    flex: 1,
    background: 'none',
    border: 'none',
    outline: 'none',
    color: Colors.textPrimary,
    fontSize: Font.sm,
  },
  dropdown: {
    position: 'absolute',
    top: 'calc(100% + 4px)',
    left: 0,
    right: 0,
    borderRadius: Radius.sm,
    backgroundColor: Colors.backgroundCard,
    border: `1px solid ${Colors.borderPrimary}`,
    zIndex: 1000,
    maxHeight: 240,
    overflowY: 'auto',
  },
  result: {
    display: 'block',
    width: '100%',
    padding: `${Gap.md}px ${Gap.xl}px`,
    textAlign: 'left' as const,
    color: Colors.textPrimary,
    fontSize: Font.sm,
    background: 'none',
    border: 'none',
    cursor: 'pointer',
  },
};
