/**
 * Reusable search bar with icon, input, and click-to-focus wrapper.
 * Used across desktop header, songs toolbar, player search modal,
 * and floating action button.
 */
import { forwardRef, useRef, useImperativeHandle, type KeyboardEventHandler } from 'react';
import { IoSearch } from 'react-icons/io5';
import { Size } from '@festival/theme';
import css from './SearchBar.module.css';

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

  const wrapperClass = className ? `${css.searchBar} ${className}` : css.searchBar;
  const inputClass = inputClassName ? `${css.searchInput} ${inputClassName}` : css.searchInput;

  return (
    <div
      className={wrapperClass}
      style={style}
      onClick={() => inputRef.current?.focus()}
    >
      {!hideIcon && <IoSearch size={Size.iconXs} className={css.searchIcon} />}
      <input
        ref={inputRef}
        className={inputClass}
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
