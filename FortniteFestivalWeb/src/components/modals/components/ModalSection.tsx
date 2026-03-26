import { memo } from 'react';
import { modalStyles } from '../modalStyles';

export interface ModalSectionProps {
  title?: string;
  hint?: string;
  children: React.ReactNode;
}

export const ModalSection = memo(function ModalSection({ title, hint, children }: ModalSectionProps) {
  return (
    <div style={modalStyles.sectionWrap}>
      {title && <div style={modalStyles.sectionTitle}>{title}</div>}
      {hint && <div style={modalStyles.sectionHint}>{hint}</div>}
      {children}
    </div>
  );
});
