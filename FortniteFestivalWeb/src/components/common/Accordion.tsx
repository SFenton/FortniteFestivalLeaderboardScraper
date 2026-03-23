/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { Size } from '@festival/theme';
import { useState } from 'react';
import { IoChevronDown } from 'react-icons/io5';
import css from '../modals/Modal.module.css';

export interface AccordionProps {
  title: string;
  hint?: string;
  icon?: React.ReactNode;
  defaultOpen?: boolean;
  children: React.ReactNode;
}

export function Accordion({ title, hint, icon, defaultOpen = false, children }: AccordionProps) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div>
      <button className={css.accordionHeader} onClick={() => setOpen(o => !o)}>
        {icon && <span className={css.accordionIcon}>{icon}</span>}
        <div className={css.accordionTitleGroup}>
          <span className={css.accordionTitle}>{title}</span>
          {hint && <span className={css.accordionHint}>{hint}</span>}
        </div>
        <IoChevronDown className={css.accordionChevron} style={{ transform: open ? 'rotate(180deg)' : 'rotate(0deg)' }} size={Size.iconChevron} />
      </button>
      <div className={css.accordionBodyWrap} style={{ gridTemplateRows: open ? '1fr' : '0fr' }}>
        <div className={css.accordionBodyInner}>{children}</div>
      </div>
    </div>
  );
}
