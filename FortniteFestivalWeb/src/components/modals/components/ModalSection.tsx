import { memo } from 'react';
import css from '../Modal.module.css';

export interface ModalSectionProps {
  title?: string;
  hint?: string;
  children: React.ReactNode;
}

export const ModalSection = memo(function ModalSection({ title, hint, children }: ModalSectionProps) {
  return (
    <div className={css.sectionWrap}>
      {title && <div className={css.sectionTitle}>{title}</div>}
      {hint && <div className={css.sectionHint}>{hint}</div>}
      {children}
    </div>
  );
});
