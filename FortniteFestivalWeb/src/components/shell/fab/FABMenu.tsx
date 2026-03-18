/**
 * FABMenu — animated popup actions menu that appears above the FAB button.
 * Renders grouped action items with dividers between groups.
 */
import { Fragment, memo } from 'react';
import type { ActionItem } from './FloatingActionButton';
import css from './FABMenu.module.css';

interface FABMenuProps {
  groups: ActionItem[][];
  visible: boolean;
  onAction: (action: ActionItem) => void;
}

const FABMenu = memo(function FABMenu({ groups, visible, onAction }: FABMenuProps) {
  return (
    <div
      className={css.menu}
      style={{
        transform: visible ? 'scale(1)' : 'scale(0)',
        opacity: visible ? 1 : 0,
        transition: visible
          ? 'transform 450ms cubic-bezier(0.34, 1.56, 0.64, 1), opacity 300ms ease'
          : 'transform 300ms ease, opacity 300ms ease',
      }}
    >
      {groups.map((group, gi) => (
        <Fragment key={gi}>
          {gi > 0 && <div className={css.divider} />}
          {group.map((action) => (
            <button key={action.label} className={css.item} onClick={() => onAction(action)}>
              <span className={css.itemIcon}>{action.icon}</span>
              {action.label}
            </button>
          ))}
        </Fragment>
      ))}
    </div>
  );
});

export default FABMenu;
