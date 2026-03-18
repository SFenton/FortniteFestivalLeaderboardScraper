/**
 * Reusable pill button with icon + label.
 * Used for sort/filter actions in toolbar headers.
 */
import css from './ActionPill.module.css';

export interface ActionPillProps {
  icon: React.ReactNode;
  label: string;
  onClick: () => void;
  /** Highlight the pill as active (e.g. filters applied). */
  active?: boolean;
  /** Show a small dot indicator. */
  dot?: boolean;
  /** Extra className on the button. */
  className?: string;
  /** Extra style (e.g. for stagger animations). */
  style?: React.CSSProperties;
}

export function ActionPill({ icon, label, onClick, active, dot, className, style }: ActionPillProps) {
  const pillClass = active ? css.pillActive : css.pill;
  const fullClass = className ? `${pillClass} ${className}` : pillClass;

  return (
    <button
      className={fullClass}
      style={style}
      onClick={onClick}
      title={label}
      aria-label={label}
    >
      {icon}
      <span>{label}</span>
      {dot && <span className={css.dot} />}
    </button>
  );
}
