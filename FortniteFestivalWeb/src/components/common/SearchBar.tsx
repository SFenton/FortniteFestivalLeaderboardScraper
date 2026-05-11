/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { forwardRef, useRef, useImperativeHandle, useLayoutEffect, useMemo, type KeyboardEventHandler, type MouseEventHandler, type PointerEventHandler, type ReactNode, type TouchEventHandler } from 'react';
import { IoSearch } from 'react-icons/io5';
import { IconSize, Colors, Font, Gap, Display, Align, Cursor, CssValue } from '@festival/theme';
import fx from '../../styles/effects.module.css';

export interface SearchBarProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  onKeyDown?: KeyboardEventHandler<HTMLInputElement>;
  onFocus?: () => void;
  /** Called when the input loses focus. */
  onBlur?: () => void;
  /** HTML enterkeyhint attribute for mobile keyboards. */
  enterKeyHint?: 'done' | 'search' | 'go' | 'send' | 'next';
  onPointerDownCapture?: PointerEventHandler<HTMLDivElement>;
  onTouchStartCapture?: TouchEventHandler<HTMLDivElement>;
  onMouseDownCapture?: MouseEventHandler<HTMLDivElement>;
  onClickCapture?: MouseEventHandler<HTMLDivElement>;
  /** Hide the search icon. Default: false. */
  hideIcon?: boolean;
  /** Extra className to apply to the outer wrapper. */
  className?: string;
  /** Extra className to apply to the input element. */
  inputClassName?: string;
  /** Extra style on the outer wrapper (e.g. for stagger animations). */
  style?: React.CSSProperties;
  /** Extra style on the search icon. */
  iconStyle?: React.CSSProperties;
  /** Search icon size. */
  iconSize?: number;
  /** Extra style on the input element. */
  inputStyle?: React.CSSProperties;
  /** Auto-focus on mount. */
  autoFocus?: boolean;
  /** Optional control rendered at the end of the search field. */
  trailing?: ReactNode;
}

export interface SearchBarRef {
  focus: (options?: FocusOptions) => void;
  blur: () => void;
}

const SearchBar = forwardRef<SearchBarRef, SearchBarProps>(function SearchBar(
  {
    value,
    onChange,
    placeholder,
    onKeyDown,
    onFocus,
    onBlur,
    enterKeyHint,
    onPointerDownCapture,
    onTouchStartCapture,
    onMouseDownCapture,
    onClickCapture,
    hideIcon,
    className,
    inputClassName,
    style,
    iconStyle,
    iconSize,
    inputStyle,
    autoFocus,
    trailing,
  },
  ref,
) {
  const inputRef = useRef<HTMLInputElement>(null);

  useImperativeHandle(ref, () => ({
    focus: (options?: FocusOptions) => inputRef.current?.focus(options),
    blur: () => inputRef.current?.blur(),
  }));

  useLayoutEffect(() => {
    if (autoFocus) inputRef.current?.focus({ preventScroll: true });
  }, [autoFocus]);

  const s = useStyles();
  const wrapperClass = className;

  return (
    <div
      className={wrapperClass}
      style={{ ...s.searchBar, ...style }}
      onPointerDownCapture={onPointerDownCapture}
      onTouchStartCapture={onTouchStartCapture}
      onMouseDownCapture={onMouseDownCapture}
      onClickCapture={onClickCapture}
      onClick={event => {
        if (event.target === inputRef.current) return;
        inputRef.current?.focus({ preventScroll: true });
      }}
    >
      {!hideIcon && <IoSearch size={iconSize ?? IconSize.xs} style={{ ...s.searchIcon, ...iconStyle }} />}
      <input
        ref={inputRef}
        className={`${fx.searchPlaceholder}${inputClassName ? ` ${inputClassName}` : ''}`}
        style={{ ...s.searchInput, ...inputStyle }}
        placeholder={placeholder}
        value={value}
        onChange={e => onChange(e.target.value)}
        onKeyDown={onKeyDown}
        onFocus={onFocus}
        onBlur={onBlur}
        enterKeyHint={enterKeyHint}
      />
      {trailing}
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
