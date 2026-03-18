import { memo } from 'react';
import css from './SectionHeader.module.css';

interface SectionHeaderProps {
  title: string;
  description?: string;
  /** When true, removes bottom margin from the description. */
  flush?: boolean;
}

/** Reusable section title + optional description used across settings, player stats, etc. */
const SectionHeader = memo(function SectionHeader({ title, description, flush }: SectionHeaderProps) {
  return (
    <>
      <div className={css.title}>{title}</div>
      {description && <div className={flush ? css.descriptionFlush : css.description}>{description}</div>}
    </>
  );
});

export default SectionHeader;
