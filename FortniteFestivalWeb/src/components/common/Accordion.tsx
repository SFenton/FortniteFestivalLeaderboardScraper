/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { Size } from '@festival/theme';
import { useState } from 'react';
import { IoChevronDown } from 'react-icons/io5';
import { modalStyles as ms } from '../modals/modalStyles';

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
      <button style={ms.accordionHeader} onClick={() => setOpen(o => !o)}>
        {icon && <span style={ms.accordionIcon}>{icon}</span>}
        <div style={ms.accordionTitleGroup}>
          <span style={ms.accordionTitle}>{title}</span>
          {hint && <span style={ms.accordionHint}>{hint}</span>}
        </div>
        <IoChevronDown style={{ ...ms.accordionChevron, transform: open ? 'rotate(180deg)' : 'rotate(0deg)' }} size={Size.iconChevron} />
      </button>
      <div style={{ ...ms.accordionBodyWrap, gridTemplateRows: open ? '1fr' : '0fr' }}>
        <div style={ms.accordionBodyInner}>{children}</div>
      </div>
    </div>
  );
}
