/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { forwardRef, useRef, useImperativeHandle, useMemo, type KeyboardEventHandler } from 'react';
import { IoSearch } from 'react-icons/io5';
import { IconSize, Colors, Font, Gap, Display, Align, Cursor, CssValue } from '@festival/theme';
import fx from '../../styles/effects.module.css';

export interface SearchBarProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  onKeyDown?: KeyboardEventHandler<HTMLInputElement>;
  onFocus?: () => void;
  /** HTML enterkeyhint attribute for mobile keyboards. */
  enterKeyHint?: 'done' | 'search' | 'go' | 'send' | 'next';
  /** Hide the search icon. Default: false. */
  hideIcon?: boolean;
  /** Extra className to apply to the outer wrapper. */
  className?: string;
  /** Extra className to apply to the input element. */
  inputClassName?: string;
  /** Extra style on the outer wrapper (e.g. for stagger animations). */
  style?: React.CSSProperties;
  /** Auto-focus on mount. */
  autoFocus?: boolean;
}

export interface SearchBarRef {
  focus: () => void;
  blur: () => void;
}

const SearchBar = forwardRef<SearchBarRef, SearchBarProps>(function SearchBar(
  {
    value,
    onChange,
    placeholder,
    onKeyDown,
    onFocus,
    enterKeyHint,
    hideIcon,
    className,
    inputClassName,
    style,
    autoFocus,
  },
  ref,
) {
  const inputRef = useRef<HTMLInputElement>(null);

  useImperativeHandle(ref, () => ({
    focus: () => inputRef.current?.focus(),
    blur: () => inputRef.current?.blur(),
  }));

  const s = useStyles();
  const wrapperClass = className;

  return (
    <div
      className={wrapperClass}
      style={{ ...s.searchBar, ...style }}
      onClick={() => inputRef.current?.focus()}
    >
      {!hideIcon && <IoSearch size={IconSize.xs} style={s.searchIcon} />}
      <input
        ref={inputRef}
        className={`${fx.searchPlaceholder}${inputClassName ? ` ${inputClassName}` : ''}`}
        style={s.searchInput}
        placeholder={placeholder}
        value={value}
        onChange={e => onChange(e.target.value)}
        onKeyDown={onKeyDown}
        onFocus={onFocus}
        enterKeyHint={enterKeyHint}
        autoFocus={autoFocus}
      />
    </div>
  );
});

export default SearchBar;

function useStyles() {
  return useMemo(() => ({
    searchBar: {
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.md,
      cursor: Cursor.text,
    },
    searchIcon: {
      color: Colors.textPrimary,
      flexShrink: 0,
    },
    searchInput: {
      flex: 1,
      background: CssValue.transparent,
      border: CssValue.none,
      outline: CssValue.none,
      color: Colors.textPrimary,
      fontSize: Font.md,
      minWidth: Gap.none,
    },
  }), []);
}
