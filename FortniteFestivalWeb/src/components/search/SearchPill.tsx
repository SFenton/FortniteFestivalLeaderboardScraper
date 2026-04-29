/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { IoSearch } from 'react-icons/io5';
import {
  Align, BoxSizing, Colors, Cursor, Display, Font, Gap, IconSize, Layout, Radius,
  TextAlign, CssValue, frostedCard, padding,
} from '@festival/theme';

interface SearchPillProps {
  onClick: () => void;
  label: string;
}


export default function SearchPill({ onClick, label }: SearchPillProps) {
  const st = useStyles();
  return (
    <div style={st.container}>
      <button type="button" style={st.button} onClick={onClick} aria-label={label}>
        <IoSearch size={IconSize.xs} style={st.icon} />
        <span>{label}</span>
      </button>
    </div>
  );
}

function useStyles() {
  return useMemo(() => ({
    container: {
      position: 'relative',
      flex: 1,
      maxWidth: Layout.searchMaxWidth,
    } as CSSProperties,
    button: {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.sm,
      width: CssValue.full,
      height: Layout.entryRowHeight,
      padding: padding(0, Gap.xl),
      borderRadius: Radius.full,
      boxSizing: BoxSizing.borderBox,
      color: Colors.textPrimary,
      fontSize: Font.md,
      cursor: Cursor.pointer,
      textAlign: TextAlign.left,
    } as CSSProperties,
    icon: {
      color: Colors.textPrimary,
      flexShrink: 0,
    } as CSSProperties,
  }), []);
}
