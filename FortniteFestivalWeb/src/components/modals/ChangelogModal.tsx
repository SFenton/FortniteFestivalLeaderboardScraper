/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useMemo, useEffect, useLayoutEffect, useState, useRef, useCallback, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoClose } from 'react-icons/io5';
import {
  Colors, Font, Weight, Gap, Radius, Border, ZIndex, Layout, LineHeight, IconSize, Opacity,
  Display, Position, Align, Justify, Cursor, Overflow, TextTransform, BoxSizing,
  CssProp, CssValue,
  padding, border, transition, transitions, scale, scaleTranslateY,
  modalOverlay, modalCard, btnPrimary, flexColumn, flexBetween, flexCenter,
  TRANSITION_MS, MODAL_SCALE_ENTER, MODAL_SLIDE_OFFSET,
} from '@festival/theme';
import { APP_VERSION } from '../../hooks/data/useVersions';
import { changelog, type ChangelogEntry } from '../../changelog';
import { useScrollMask } from '../../hooks/ui/useScrollMask';

export default function ChangelogModal({ onDismiss }: { onDismiss: () => void }) {
  const { t } = useTranslation();
  const [animIn, setAnimIn] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);
  const s = useStyles(animIn);

  useLayoutEffect(() => {
    const id = requestAnimationFrame(() => setAnimIn(true));
    return () => cancelAnimationFrame(id);
  }, []);

  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onDismiss(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [onDismiss]);

  const updateMask = useScrollMask(scrollRef, [animIn]);
  const handleScroll = useCallback(() => updateMask(), [updateMask]);

  return (
    <div style={s.overlay} onClick={onDismiss}>
      <div style={s.card} onClick={e => e.stopPropagation()}>
        <div style={s.header}>
          <h2 style={s.title}>{t('changelog.title')} <span style={s.dot}>·</span> {APP_VERSION}</h2>
          <button style={s.closeBtn} onClick={onDismiss} aria-label="Close">
            <IoClose size={18} />
          </button>
        </div>

        <div ref={scrollRef} onScroll={handleScroll} style={s.content}>
          {changelog.map((entry: ChangelogEntry, ei) => (
            <div key={ei} style={s.entry}>
              {entry.sections.map((section, si) => (
                <div key={si} style={si > 0 ? { marginTop: Gap.section } : undefined}>
                  <div style={s.sectionTitle}>{section.title}</div>
                  <ul style={s.changeList}>
                    {section.items.map((item, i) => (
                      <li key={i} style={s.changeItem}>{item}</li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          ))}
        </div>

        <div style={s.footer}>
          <button style={s.dismissBtn} onClick={onDismiss}>
            {t('common.dismiss')}
          </button>
        </div>
      </div>
    </div>
  );
}

function useStyles(animIn: boolean) {
  return useMemo(() => ({
    overlay: {
      ...modalOverlay,
      zIndex: ZIndex.changelogOverlay,
      padding: padding(Gap.section),
      opacity: animIn ? 1 : Opacity.none,
      transition: transition(CssProp.opacity, TRANSITION_MS),
    } as CSSProperties,
    card: {
      ...modalCard,
      ...flexColumn,
      borderRadius: Radius.lg,
      width: CssValue.full,
      maxWidth: Layout.changelogMaxWidth,
      maxHeight: Layout.changelogMaxHeight,
      overflow: Overflow.hidden,
      opacity: animIn ? 1 : Opacity.none,
      transform: animIn ? scaleTranslateY(1, 0) : scaleTranslateY(MODAL_SCALE_ENTER, MODAL_SLIDE_OFFSET),
      transition: transitions(
        transition(CssProp.opacity, TRANSITION_MS),
        transition(CssProp.transform, TRANSITION_MS),
      ),
    } as CSSProperties,
    header: {
      ...flexBetween,
      padding: padding(Gap.xl, Font.lg, Gap.xl, Gap.section),
      flexShrink: 0,
    } as CSSProperties,
    title: {
      fontSize: Font.xl,
      fontWeight: Weight.bold,
      margin: Gap.none,
    } as CSSProperties,
    dot: {
      color: Colors.textTertiary,
      margin: padding(0, Gap.xs),
    } as CSSProperties,
    closeBtn: {
      width: Layout.closeBtnSize,
      height: Layout.closeBtnSize,
      borderRadius: CssValue.circle,
      background: Colors.surfaceElevated,
      border: border(Border.thin, Colors.borderPrimary),
      color: Colors.textSecondary,
      ...flexCenter,
      cursor: Cursor.pointer,
      flexShrink: 0,
    } as CSSProperties,
    content: {
      flex: 1,
      overflowY: Overflow.auto,
      padding: padding(0, Gap.section),
    } as CSSProperties,
    entry: {
      marginBottom: Gap.section,
    } as CSSProperties,
    sectionTitle: {
      fontSize: Font.md,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
      marginBottom: Gap.sm,
      textTransform: TextTransform.uppercase,
      letterSpacing: Font.letterSpacingWide,
    } as CSSProperties,
    changeList: {
      margin: Gap.none,
      paddingLeft: Gap.section,
    } as CSSProperties,
    changeItem: {
      fontSize: Font.md,
      color: Colors.textSecondary,
      lineHeight: LineHeight.loose,
      marginBottom: Gap.sm,
    } as CSSProperties,
    footer: {
      padding: padding(Gap.xl, Gap.section),
      flexShrink: 0,
    } as CSSProperties,
    dismissBtn: {
      ...btnPrimary,
      width: CssValue.full,
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      padding: padding(Gap.xl),
    } as CSSProperties,
  }), [animIn]);
}
