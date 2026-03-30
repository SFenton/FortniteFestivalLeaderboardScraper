/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * FABMenu — animated popup actions menu that appears above the FAB button.
 * Renders grouped action items with dividers between groups.
 */
import { Fragment, memo, useMemo, type CSSProperties } from 'react';
import { Colors, Font, Gap, Radius, Layout, Shadow, ZIndex, Position, Cursor, TextAlign, WhiteSpace, PointerEvents, Overflow, IconSize, CssValue, TransformOrigin, flexRow, flexCenter, padding, scale, EASE_OVERSHOOT, FAB_OPEN_MS, TRANSITION_MS } from '@festival/theme';
import type { ActionItem } from './FloatingActionButton';

interface FABMenuProps {
  groups: ActionItem[][];
  visible: boolean;
  onAction: (action: ActionItem) => void;
}

const FABMenu = memo(function FABMenu({ groups, visible, onAction }: FABMenuProps) {
  const s = useFABMenuStyles(visible);
  return (
    <div
      style={s.menu}
      data-glow-scope=""
    >
      {groups.map((group, gi) => (
        <Fragment key={gi}>
          {gi > 0 && <div style={s.divider} />}
          {group.map((action) => (
            <button key={action.label} style={s.item} onClick={() => onAction(action)}>
              <span style={s.itemIcon}>{action.icon}</span>
              {action.label}
            </button>
          ))}
        </Fragment>
      ))}
    </div>
  );
});

export default FABMenu;

function useFABMenuStyles(visible: boolean) {
  return useMemo(() => ({
    menu: {
      position: Position.absolute,
      bottom: Layout.fabMenuBottom,
      right: Gap.none,
      zIndex: ZIndex.confirmOverlay,
      pointerEvents: PointerEvents.auto,
      backgroundColor: Colors.backgroundCard,
      borderRadius: Radius.sm,
      overflow: Overflow.hidden,
      minWidth: Layout.fabMenuMinWidth,
      whiteSpace: WhiteSpace.nowrap,
      boxShadow: Shadow.tooltip,
      transformOrigin: TransformOrigin.bottomRight,
      transform: visible ? scale(1) : scale(0),
      opacity: visible ? 1 : 0,
      transition: visible
        ? `transform ${FAB_OPEN_MS}ms ${EASE_OVERSHOOT}, opacity ${TRANSITION_MS}ms ease`
        : `transform ${TRANSITION_MS}ms ease, opacity ${TRANSITION_MS}ms ease`,
    } as CSSProperties,
    item: {
      '--frosted-card': '1',
      ...flexRow,
      gap: Gap.xl,
      width: CssValue.full,
      padding: padding(Gap.xl + Gap.sm, Gap.section),
      background: CssValue.none,
      border: CssValue.none,
      boxShadow: 'inset 0 0 0 100vmax rgba(255, 255, 255, calc(0.03 * var(--glow-hover, 0)))',
      color: Colors.textSecondary,
      fontSize: Font.md,
      cursor: Cursor.pointer,
      textAlign: TextAlign.left,
      position: Position.relative,
      overflow: Overflow.hidden,
    } as CSSProperties,
    itemIcon: {
      ...flexCenter,
      width: IconSize.default,
      flexShrink: 0,
      color: Colors.textTertiary,
    } as CSSProperties,
    divider: {
      height: 1,
      backgroundColor: Colors.glassBorder,
    } as CSSProperties,
  }), [visible]);
}
