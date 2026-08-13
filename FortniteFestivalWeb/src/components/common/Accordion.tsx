import { Size } from '@festival/theme';
import { useId, useRef, useState, type ReactNode } from 'react';
import { IoChevronDown } from 'react-icons/io5';
import { modalStyles as ms } from '../modals/modalStyles';
import PressableButton from './PressableButton';

export interface AccordionProps {
  title: string;
  hint?: string;
  icon?: ReactNode;
  defaultOpen?: boolean;
  panelLandmark?: boolean;
  children: ReactNode;
}

export function Accordion({
  title,
  hint,
  icon,
  defaultOpen = false,
  panelLandmark = false,
  children,
}: AccordionProps) {
  const [open, setOpen] = useState(defaultOpen);
  const baseId = useId();
  const triggerId = `${baseId}-trigger`;
  const panelId = `${baseId}-panel`;
  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  const toggle = () => {
    if (open && panelRef.current?.contains(document.activeElement)) {
      triggerRef.current?.focus({ preventScroll: true });
    }
    setOpen(current => !current);
  };

  return (
    <div>
      <PressableButton
        ref={triggerRef}
        id={triggerId}
        style={ms.accordionHeader}
        aria-expanded={open}
        aria-controls={panelId}
        onPress={toggle}
      >
        {icon && <span style={ms.accordionIcon}>{icon}</span>}
        <div style={ms.accordionTitleGroup}>
          <span style={ms.accordionTitle}>{title}</span>
          {hint && <span style={ms.accordionHint}>{hint}</span>}
        </div>
        <IoChevronDown style={{ ...ms.accordionChevron, transform: open ? 'rotate(180deg)' : 'rotate(0deg)' }} size={Size.iconChevron} />
      </PressableButton>
      <div
        ref={panelRef}
        id={panelId}
        style={{ ...ms.accordionBodyWrap, gridTemplateRows: open ? '1fr' : '0fr' }}
        inert={!open}
        aria-hidden={open ? undefined : true}
        role={panelLandmark ? 'region' : undefined}
        aria-labelledby={panelLandmark ? triggerId : undefined}
      >
        <div style={ms.accordionBodyInner}>{children}</div>
      </div>
    </div>
  );
}
